﻿using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Ombi.Api.Plex;
using Ombi.Api.Plex.Models;
using Ombi.Core.Authentication;
using Ombi.Core.Engine;
using Ombi.Core.Engine.Interfaces;
using Ombi.Core.Models.Requests;
using Ombi.Core.Settings;
using Ombi.Core.Settings.Models.External;
using Ombi.Helpers;
using Ombi.Hubs;
using Ombi.Store.Entities;
using Ombi.Store.Entities.Requests;
using Quartz;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ombi.Schedule.Jobs.Plex
{
    public class PlexWatchlistImport : IPlexWatchlistImport
    {
        private readonly IPlexApi _plexApi;
        private readonly ISettingsService<PlexSettings> _settings;
        private readonly OmbiUserManager _ombiUserManager;
        private readonly IMovieRequestEngine _movieRequestEngine;
        private readonly ITvRequestEngine _tvRequestEngine;
        private readonly IHubContext<NotificationHub> _hub;
        private readonly ILogger _logger;

        public PlexWatchlistImport(IPlexApi plexApi, ISettingsService<PlexSettings> settings, OmbiUserManager ombiUserManager,
            IMovieRequestEngine movieRequestEngine, ITvRequestEngine tvRequestEngine, IHubContext<NotificationHub> hub,
            ILogger<PlexWatchlistImport> logger)
        {
            _plexApi = plexApi;
            _settings = settings;
            _ombiUserManager = ombiUserManager;
            _movieRequestEngine = movieRequestEngine;
            _tvRequestEngine = tvRequestEngine;
            _hub = hub;
            _logger = logger;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            var settings = await _settings.GetSettingsAsync();
            if (!settings.Enable || !settings.EnableWatchlistImport)
            {
                _logger.LogDebug($"Not enabled. Plex Enabled: {settings.Enable}, Watchlist Enabled: {settings.EnableWatchlistImport}");
                return;
            }

            var plexUsersWithTokens = _ombiUserManager.Users.Where(x => x.UserType == UserType.PlexUser && x.MediaServerToken != null).ToList();
            _logger.LogInformation($"Found {plexUsersWithTokens.Count} users with tokens");
            await NotifyClient("Starting Watchlist Import");

            foreach (var user in plexUsersWithTokens)
            {
                try
                {

                    _logger.LogDebug($"Starting Watchlist Import for {user.UserName} with token {user.MediaServerToken}");
                    var watchlist = await _plexApi.GetWatchlist(user.MediaServerToken, context?.CancellationToken ?? CancellationToken.None);
                    if (watchlist == null || !(watchlist.MediaContainer?.Metadata?.Any() ?? false))
                    {
                        _logger.LogDebug($"No watchlist found for {user.UserName}");
                        return;
                    }

                    var items = watchlist.MediaContainer.Metadata;
                    _logger.LogDebug($"Items found in watchlist: {watchlist.MediaContainer.totalSize}");
                    foreach (var item in items)
                    {
                        _logger.LogDebug($"Processing {item.title} {item.type}");
                        var providerIds = await GetProviderIds(user.MediaServerToken, item, context?.CancellationToken ?? CancellationToken.None);
                        if (!providerIds.TheMovieDb.HasValue())
                        {
                            _logger.LogWarning($"No TheMovieDb Id found for {item.title}, could not import via Plex WatchList");
                            // We need a MovieDbId to support this;
                            return;
                        }
                        switch (item.type)
                        {
                            case "show":
                                await ProcessShow(int.Parse(providerIds.TheMovieDb), user);
                                break;
                            case "movie":
                                await ProcessMovie(int.Parse(providerIds.TheMovieDb), user);
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Exception thrown when importing watchlist for user {user.UserName}");
                    continue;
                }
            }

            await NotifyClient("Finished Watchlist Import");
        }

        private async Task ProcessMovie(int theMovieDbId, OmbiUser user)
        {
            _movieRequestEngine.SetUser(user);
            var response = await _movieRequestEngine.RequestMovie(new() { TheMovieDbId = theMovieDbId, Source = RequestSource.PlexWatchlist });
            if (response.IsError)
            {
                if (response.ErrorCode == ErrorCode.AlreadyRequested)
                {
                    _logger.LogDebug($"Movie already requested for user '{user.UserName}'");
                    return;
                }
                _logger.LogInformation($"Error adding title from PlexWatchlist for user '{user.UserName}'. Message: '{response.ErrorMessage}'");
            }
            else
            {
                _logger.LogInformation($"Added title from PlexWatchlist for user '{user.UserName}'. {response.Message}");
            }
        }


        private async Task ProcessShow(int theMovieDbId, OmbiUser user)
        {
            _tvRequestEngine.SetUser(user);
            var response = await _tvRequestEngine.RequestTvShow(new TvRequestViewModelV2 { RequestAll = true, TheMovieDbId = theMovieDbId, Source = RequestSource.PlexWatchlist });
            if (response.IsError)
            {
                if (response.ErrorCode == ErrorCode.AlreadyRequested)
                {
                    _logger.LogDebug($"Show already requested for user '{user.UserName}'");
                    return;
                }
                _logger.LogInformation($"Error adding title from PlexWatchlist for user '{user.UserName}'. Message: '{response.ErrorMessage}'");
            }
            else
            {
                _logger.LogInformation($"Added title from PlexWatchlist for user '{user.UserName}'. {response.Message}");
            }
        }

        private async Task<ProviderId> GetProviderIds(string authToken, Metadata movie, CancellationToken cancellationToken)
        {
            var guids = new List<string>();
            if (!movie.Guid.Any())
            {
                var metaData = await _plexApi.GetWatchlistMetadata(movie.ratingKey, authToken, cancellationToken);

                var meta = metaData.MediaContainer.Metadata.FirstOrDefault();
                guids.Add(meta.guid);
                if (meta.Guid != null)
                {
                    foreach (var g in meta.Guid)
                    {
                        guids.Add(g.Id);
                    }
                }
            }
            else
            {
                // Currently a Plex Pass feature only
                foreach (var g in movie.Guid)
                {
                    guids.Add(g.Id);
                }
            }
            var providerIds = PlexHelper.GetProviderIdsFromMetadata(guids.ToArray());
            return providerIds;
        }

        private async Task NotifyClient(string message)
        {
            if (_hub?.Clients == null)
            {
                return;
            }
            await _hub?.Clients?.Clients(NotificationHub.AdminConnectionIds)?
                .SendAsync(NotificationHub.NotificationEvent, $"Plex Watchlist Import - {message}");
        }
        public void Dispose() { }
    }
}
