using Gridder.Models;
using Gridder.ViewModels;

namespace Gridder.Views;

public partial class WaveformEditorView : ContentView
{
    private WaveformEditorViewModel? _viewModel;
    private AudioTrack? _subscribedTrack;

    public WaveformEditorView()
    {
        InitializeComponent();
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();

        if (BindingContext is WaveformEditorViewModel vm)
        {
            _viewModel = vm;
            WaveformCanvas.ViewModel = vm;

            // Redraw when key properties change
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(WaveformEditorViewModel.ScrollPositionSeconds)
                    or nameof(WaveformEditorViewModel.PixelsPerSecond)
                    or nameof(WaveformEditorViewModel.WaveformData)
                    or nameof(WaveformEditorViewModel.BeatGrid)
                    or nameof(WaveformEditorViewModel.SelectedBeatIndex)
                    or nameof(WaveformEditorViewModel.PlaybackPositionSeconds))
                {
                    WaveformCanvas.InvalidateSurface();
                }

                // Subscribe to Track.PropertyChanged when track changes
                if (e.PropertyName == nameof(WaveformEditorViewModel.Track))
                    SubscribeToTrack(vm.Track);
            };

            // Subscribe to initial track if already loaded
            SubscribeToTrack(vm.Track);

            // Click-to-seek on waveform
            var tap = new TapGestureRecognizer();
            tap.Tapped += OnWaveformTapped;
            WaveformCanvas.GestureRecognizers.Add(tap);
        }
    }

    private void SubscribeToTrack(AudioTrack? track)
    {
        if (_subscribedTrack != null)
            _subscribedTrack.PropertyChanged -= OnTrackPropertyChanged;

        _subscribedTrack = track;

        if (_subscribedTrack != null)
            _subscribedTrack.PropertyChanged += OnTrackPropertyChanged;
    }

    private void OnTrackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioTrack.FirstBeatOverride))
            WaveformCanvas.InvalidateSurface();
    }

    private void OnWaveformTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel == null) return;

        var position = e.GetPosition(WaveformCanvas);
        if (position == null) return;

        var time = _viewModel.XToTime((float)position.Value.X);
        time = Math.Clamp(time, 0, _viewModel.TotalDurationSeconds);
        _viewModel.RequestSeek(time);
    }
}
