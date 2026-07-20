using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vertex.App.ViewModels;
using Vertex.Shared;

namespace Vertex.App.Views;

public sealed partial class ConnectScreen : UserControl
{
    public TunnelViewModel ViewModel { get; }

    public ConnectScreen(TunnelViewModel vm)
    {
        ViewModel = vm;
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Refresh();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        Hero.State                = ViewModel.State;
        Hero.UploadBytesPerSec    = ViewModel.UploadBytesPerSec;
        Hero.DownloadBytesPerSec  = ViewModel.DownloadBytesPerSec;
        Pill.State           = ViewModel.State;
        Pill.AssignedIp      = ViewModel.AssignedIp;
        ConnectButton.State  = ViewModel.State;

        // Speed pill renders only while a session is live; collapsed
        // when disconnected (rate is 0 and ping is meaningless).
        Speed.Visibility = ViewModel.IsConnected
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        Speed.UploadBytesPerSec   = ViewModel.UploadBytesPerSec;
        Speed.DownloadBytesPerSec = ViewModel.DownloadBytesPerSec;
        Speed.PingMs              = ViewModel.PingMs;

        StatusText.Text = ViewModel.State switch
        {
            ConnectionState.Connected     => "Connected",
            ConnectionState.Connecting    => "Connecting…",
            ConnectionState.Handshaking   => "Handshaking…",
            ConnectionState.Reconnecting  => "Reconnecting…",
            _                              => "Disconnected",
        };

        // Card is disabled while connected/transitioning — paritет с macOS
        // ConnectScreen.swift:100 (`isDisabled: viewModel.isConnected ||
        // viewModel.isTransitioning`). Pickers write SelectedBroker
        // immediately but the engine applies it on next reconnect, so
        // hiding the affordance during the active session avoids
        // surfacing a pin that has no effect until disconnect+reconnect.
        ServerCardView.IsCardEnabled     = !ViewModel.IsConnected && !ViewModel.IsBusy;
        ServerCardView.VertexDisplayName = ViewModel.ResolvedBrokerDisplay;
        ServerCardView.EdgeDisplayName   = ViewModel.ResolvedExitDisplay;
        ServerCardView.VertexCodeText    = null;
        ServerCardView.EdgeCodeText      = null;

        if (string.IsNullOrEmpty(ViewModel.LastErrorMessage))
        {
            ErrorBanner.Visibility = Visibility.Collapsed;
        }
        else
        {
            ErrorBanner.Visibility = Visibility.Visible;
            ErrorText.Text = ViewModel.LastErrorMessage;
        }
    }

    private async void OnConnectTapped(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel.IsConnected || ViewModel.IsBusy)
                await ViewModel.DisconnectCommand.ExecuteAsync(null);
            else
                await ViewModel.ConnectCommand.ExecuteAsync(null);
        }
        catch (System.Exception ex)
        {
            ViewModel.LastErrorMessage = ex.Message;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, e);

    private async void OnBrokerTapped(object sender, RoutedEventArgs e)
    {
        var dlg = new BrokerListDialog(ViewModel, this.XamlRoot);
        await dlg.ShowAsync();
    }

    private async void OnExitTapped(object sender, RoutedEventArgs e)
    {
        var dlg = new ExitListDialog(ViewModel, this.XamlRoot);
        await dlg.ShowAsync();
    }

    public event RoutedEventHandler? SettingsRequested;
}
