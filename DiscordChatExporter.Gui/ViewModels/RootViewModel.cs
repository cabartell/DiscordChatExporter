﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Models;
using DiscordChatExporter.Core.Models.Exceptions;
using DiscordChatExporter.Core.Services;
using DiscordChatExporter.Core.Services.Exceptions;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.ViewModels.Components;
using DiscordChatExporter.Gui.ViewModels.Framework;
using Gress;
using MaterialDesignThemes.Wpf;
using Stylet;
using Tyrrrz.Extensions;

namespace DiscordChatExporter.Gui.ViewModels
{
    public class RootViewModel : Screen
    {
        private readonly IViewModelFactory _viewModelFactory;
        private readonly DialogManager _dialogManager;
        private readonly SettingsService _settingsService;
        private readonly UpdateService _updateService;
        private readonly DataService _dataService;
        private readonly ExportService _exportService;

        public ISnackbarMessageQueue Notifications { get; } = new SnackbarMessageQueue(TimeSpan.FromSeconds(5));

        public IProgressManager ProgressManager { get; } = new ProgressManager();

        public bool IsBusy { get; private set; }

        public bool IsProgressIndeterminate { get; private set; }

        public bool IsBotToken { get; set; }

        public string? TokenValue { get; set; }

        public IReadOnlyList<GuildViewModel>? AvailableGuilds { get; private set; }

        public GuildViewModel? SelectedGuild { get; set; }

        public IReadOnlyList<ChannelViewModel>? SelectedChannels { get; set; }

        public RootViewModel(IViewModelFactory viewModelFactory, DialogManager dialogManager,
            SettingsService settingsService, UpdateService updateService, DataService dataService,
            ExportService exportService)
        {
            _viewModelFactory = viewModelFactory;
            _dialogManager = dialogManager;
            _settingsService = settingsService;
            _updateService = updateService;
            _dataService = dataService;
            _exportService = exportService;

            // Set title
            DisplayName = $"{App.Name} v{App.VersionString}";

            // Update busy state when progress manager changes
            ProgressManager.Bind(o => o.IsActive, (sender, args) => IsBusy = ProgressManager.IsActive);
            ProgressManager.Bind(o => o.IsActive,
                (sender, args) => IsProgressIndeterminate = ProgressManager.IsActive && ProgressManager.Progress.IsEither(0, 1));
            ProgressManager.Bind(o => o.Progress,
                (sender, args) => IsProgressIndeterminate = ProgressManager.IsActive && ProgressManager.Progress.IsEither(0, 1));
        }

        private async Task HandleAutoUpdateAsync()
        {
            try
            {
                // Check for updates
                var updateVersion = await _updateService.CheckForUpdatesAsync();
                if (updateVersion == null)
                    return;

                // Notify user of an update and prepare it
                Notifications.Enqueue($"Downloading update to {App.Name} v{updateVersion}...");
                await _updateService.PrepareUpdateAsync(updateVersion);

                // Prompt user to install update (otherwise install it when application exits)
                Notifications.Enqueue(
                    "Update has been downloaded and will be installed when you exit",
                    "INSTALL NOW", () =>
                    {
                        _updateService.FinalizeUpdate(true);
                        RequestClose();
                    });
            }
            catch
            {
                // Failure to update shouldn't crash the application
                Notifications.Enqueue("Failed to perform application update");
            }
        }

        protected override async void OnViewLoaded()
        {
            base.OnViewLoaded();

            // Load settings
            _settingsService.Load();

            // Get last token
            if (_settingsService.LastToken != null)
            {
                IsBotToken = _settingsService.LastToken.Type == AuthTokenType.Bot;
                TokenValue = _settingsService.LastToken.Value;
            }

            // Check and prepare update
            await HandleAutoUpdateAsync();
        }

        protected override void OnClose()
        {
            base.OnClose();

            // Save settings
            _settingsService.Save();

            // Finalize updates if necessary
            _updateService.FinalizeUpdate(false);
        }

        public async void ShowSettings()
        {
            // Create dialog
            var dialog = _viewModelFactory.CreateSettingsViewModel();

            // Show dialog
            await _dialogManager.ShowDialogAsync(dialog);
        }

        public bool CanPopulateGuildsAndChannels => !IsBusy && !string.IsNullOrWhiteSpace(TokenValue);

        public async void PopulateGuildsAndChannels()
        {
            // Create progress operation
            var operation = ProgressManager.CreateOperation();

            try
            {
                // Sanitize token
                TokenValue = TokenValue!.Trim('"');

                // Create token
                var token = new AuthToken(
                    IsBotToken ? AuthTokenType.Bot : AuthTokenType.User,
                    TokenValue);

                // Save token
                _settingsService.LastToken = token;

                // Prepare available guild list
                var availableGuilds = new List<GuildViewModel>();

                // Get direct messages
                {
                    var guild = Guild.DirectMessages;
                    var channels = await _dataService.GetDirectMessageChannelsAsync(token);

                    // Create channel view models
                    var channelViewModels = new List<ChannelViewModel>();
                    foreach (var channel in channels)
                    {
                        // Get fake category
                        var category = channel.Type == ChannelType.DirectTextChat ? "Private" : "Group";

                        // Create channel view model
                        var channelViewModel = _viewModelFactory.CreateChannelViewModel(channel, category);

                        // Add to list
                        channelViewModels.Add(channelViewModel);
                    }

                    // Create guild view model
                    var guildViewModel = _viewModelFactory.CreateGuildViewModel(guild,
                        channelViewModels.OrderBy(c => c.Category)
                            .ThenBy(c => c.Model!.Name)
                            .ToArray());

                    // Add to list
                    availableGuilds.Add(guildViewModel);
                }

                // Get guilds
                var guilds = await _dataService.GetUserGuildsAsync(token);
                foreach (var guild in guilds)
                {
                    var channels = await _dataService.GetGuildChannelsAsync(token, guild.Id);
                    var categoryChannels = channels.Where(c => c.Type == ChannelType.GuildCategory).ToArray();
                    var exportableChannels = channels.Where(c => c.Type.IsExportable()).ToArray();

                    // Create channel view models
                    var channelViewModels = new List<ChannelViewModel>();
                    foreach (var channel in exportableChannels)
                    {
                        // Get category
                        var category = categoryChannels.FirstOrDefault(c => c.Id == channel.ParentId)?.Name;

                        // Create channel view model
                        var channelViewModel = _viewModelFactory.CreateChannelViewModel(channel, category);

                        // Add to list
                        channelViewModels.Add(channelViewModel);
                    }

                    // Create guild view model
                    var guildViewModel = _viewModelFactory.CreateGuildViewModel(guild,
                        channelViewModels.OrderBy(c => c.Category)
                            .ThenBy(c => c.Model!.Name)
                            .ToArray());

                    // Add to list
                    availableGuilds.Add(guildViewModel);
                }

                // Update available guild list
                AvailableGuilds = availableGuilds;

                // Pre-select first guild
                SelectedGuild = AvailableGuilds.FirstOrDefault();
            }
            catch (HttpErrorStatusCodeException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                Notifications.Enqueue("Unauthorized – make sure the token is valid");
            }
            catch (HttpErrorStatusCodeException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                Notifications.Enqueue("Forbidden – account may be locked by 2FA");
            }
            catch (DomainException ex)
            {
                Notifications.Enqueue(ex.Message);
            }
            finally
            {
                operation.Dispose();
            }
        }

        public bool CanExportChannels => !IsBusy && SelectedGuild != null && SelectedChannels != null && SelectedChannels.Any();

        public async void ExportChannels()
        {
            // Get last used token
            var token = _settingsService.LastToken!;

            // Create dialog
            var dialog = _viewModelFactory.CreateExportSetupViewModel(SelectedGuild!, SelectedChannels!);

            // Show dialog, if canceled - return
            if (await _dialogManager.ShowDialogAsync(dialog) != true)
                return;

            // Create a progress operation for each channel to export
            var operations = ProgressManager.CreateOperations(dialog.Channels!.Count);

            // Export channels
            var successfulExportCount = 0;
            for (var i = 0; i < dialog.Channels.Count; i++)
            {
                var operation = operations[i];
                var channel = dialog.Channels[i];

                try
                {
                    await _exportService.ExportChatLogAsync(token, dialog.Guild!, channel!,
                        dialog.OutputPath!, dialog.SelectedFormat, dialog.PartitionLimit,
                        dialog.After, dialog.Before, operation);

                    successfulExportCount++;
                }
                catch (HttpErrorStatusCodeException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
                {
                    Notifications.Enqueue($"You don't have access to channel [{channel.Model!.Name}]");
                }
                catch (HttpErrorStatusCodeException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    Notifications.Enqueue($"Channel [{channel.Model!.Name}] doesn't exist");
                }
                catch (DomainException ex)
                {
                    Notifications.Enqueue(ex.Message);
                }
                finally
                {
                    operation.Dispose();
                }
            }

            // Notify of overall completion
            if (successfulExportCount > 0)
                Notifications.Enqueue($"Successfully exported {successfulExportCount} channel(s)");
        }
    }
}