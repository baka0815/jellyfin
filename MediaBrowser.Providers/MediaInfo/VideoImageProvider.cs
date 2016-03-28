﻿using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommonIO;

namespace MediaBrowser.Providers.MediaInfo
{
    public class VideoImageProvider : IDynamicImageProvider, IHasItemChangeMonitor, IHasOrder
    {
        private readonly IIsoManager _isoManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IServerConfigurationManager _config;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;

        public VideoImageProvider(IIsoManager isoManager, IMediaEncoder mediaEncoder, IServerConfigurationManager config, ILibraryManager libraryManager, ILogger logger, IFileSystem fileSystem)
        {
            _isoManager = isoManager;
            _mediaEncoder = mediaEncoder;
            _config = config;
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
        }

        /// <summary>
        /// The null mount task result
        /// </summary>
        protected readonly Task<IIsoMount> NullMountTaskResult = Task.FromResult<IIsoMount>(null);

        /// <summary>
        /// Mounts the iso if needed.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{IIsoMount}.</returns>
        protected Task<IIsoMount> MountIsoIfNeeded(Video item, CancellationToken cancellationToken)
        {
            if (item.VideoType == VideoType.Iso)
            {
                return _isoManager.Mount(item.Path, cancellationToken);
            }

            return NullMountTaskResult;
        }

        public IEnumerable<ImageType> GetSupportedImages(IHasImages item)
        {
            return new List<ImageType> { ImageType.Primary };
        }

        public Task<DynamicImageResponse> GetImage(IHasImages item, ImageType type, CancellationToken cancellationToken)
        {
            var video = (Video)item;

            // No support for this
            if (video.VideoType == VideoType.HdDvd || video.IsPlaceHolder)
            {
                return Task.FromResult(new DynamicImageResponse { HasImage = false });
            }

            // Can't extract from iso's if we weren't unable to determine iso type
            if (video.VideoType == VideoType.Iso && !video.IsoType.HasValue)
            {
                return Task.FromResult(new DynamicImageResponse { HasImage = false });
            }

            // Can't extract if we didn't find a video stream in the file
            if (!video.DefaultVideoStreamIndex.HasValue)
            {
                _logger.Info("Skipping image extraction due to missing DefaultVideoStreamIndex for {0}.", video.Path ?? string.Empty);
                return Task.FromResult(new DynamicImageResponse { HasImage = false });
            }

            return GetVideoImage(video, cancellationToken);
        }

        public async Task<DynamicImageResponse> GetVideoImage(Video item, CancellationToken cancellationToken)
        {
            var isoMount = await MountIsoIfNeeded(item, cancellationToken).ConfigureAwait(false);

            try
            {
                // If we know the duration, grab it from 10% into the video. Otherwise just 10 seconds in.
                // Always use 10 seconds for dvd because our duration could be out of whack
                var imageOffset = item.VideoType != VideoType.Dvd && item.RunTimeTicks.HasValue &&
                                  item.RunTimeTicks.Value > 0
                                      ? TimeSpan.FromTicks(Convert.ToInt64(item.RunTimeTicks.Value * .1))
                                      : TimeSpan.FromSeconds(10);

                var protocol = item.LocationType == LocationType.Remote
                    ? MediaProtocol.Http
                    : MediaProtocol.File;

                var inputPath = MediaEncoderHelpers.GetInputArgument(_fileSystem, item.Path, protocol, isoMount, item.PlayableStreamFileNames);

                var stream = await _mediaEncoder.ExtractVideoImage(inputPath, protocol, item.Video3DFormat, imageOffset, cancellationToken).ConfigureAwait(false);

                return new DynamicImageResponse
                {
                    Format = ImageFormat.Jpg,
                    HasImage = true,
                    Stream = stream
                };
            }
            finally
            {
                if (isoMount != null)
                {
                    isoMount.Dispose();
                }
            }
        }

        public string Name
        {
            get { return "Screen Grabber"; }
        }

        public bool Supports(IHasImages item)
        {
            var video = item as Video;

            if (item.LocationType == LocationType.FileSystem && video != null && !video.IsPlaceHolder &&
                !video.IsShortcut && !video.IsArchive)
            {
                return true;
            }

            return false;
        }

        public int Order
        {
            get
            {
                // Make sure this comes after internet image providers
                return 100;
            }
        }

        public bool HasChanged(IHasMetadata item, MetadataStatus status, IDirectoryService directoryService)
        {
            if (status.ItemDateModified.HasValue)
            {
                if (status.ItemDateModified.Value != item.DateModified)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
