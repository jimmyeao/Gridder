using Gridder.Models;

namespace Gridder.Services;

public class TrackMetadataService : ITrackMetadataService
{
    public Task ReadMetadataAsync(AudioTrack track)
    {
        return Task.Run(() =>
        {
            using var file = TagLib.File.Create(track.FilePath);

            track.Title = file.Tag.Title ?? Path.GetFileNameWithoutExtension(track.FilePath);
            track.Artist = file.Tag.FirstPerformer ?? string.Empty;
            track.Duration = file.Properties.Duration;

            // Check for existing Serato BeatGrid data
            track.HasExistingBeatGrid = HasSeratoBeatGrid(file);
        });
    }

    private static bool HasSeratoBeatGrid(TagLib.File file)
    {
        // MP3: check for GEOB frame named "Serato BeatGrid"
        if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3Tag)
        {
            var frames = id3Tag.GetFrames<TagLib.Id3v2.AttachmentFrame>();
            foreach (var frame in frames)
            {
                if (frame.Description == "Serato BeatGrid")
                    return true;
            }
        }

        // FLAC: check for SERATO_BEATGRID Vorbis comment
        if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
        {
            var field = xiph.GetField("SERATO_BEATGRID");
            if (field != null && field.Length > 0)
                return true;
        }

        return false;
    }
}
