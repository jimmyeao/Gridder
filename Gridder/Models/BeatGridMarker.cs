using CommunityToolkit.Mvvm.ComponentModel;

namespace Gridder.Models;

public partial class BeatGridMarker : ObservableObject
{
    [ObservableProperty]
    private double _positionSeconds;

    /// <summary>
    /// For non-terminal markers: number of beats until the next marker.
    /// Null for terminal markers.
    /// </summary>
    public int? BeatsUntilNext { get; set; }

    /// <summary>
    /// For terminal markers: BPM at this point onward.
    /// Null for non-terminal markers.
    /// </summary>
    public double? Bpm { get; set; }

    public bool IsTerminal => Bpm.HasValue;
}
