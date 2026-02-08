using Microsoft.Extensions.Logging;
using Plugin.Maui.Audio;
using SkiaSharp.Views.Maui.Controls.Hosting;
using Gridder.Services;
using Gridder.ViewModels;

namespace Gridder;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseSkiaSharp()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Services
        builder.Services.AddSingleton<IFolderPickerService, FolderPickerService>();
        builder.Services.AddSingleton<ITrackMetadataService, TrackMetadataService>();
        builder.Services.AddSingleton<ILibraryScanService, LibraryScanService>();
        builder.Services.AddSingleton<IPythonAnalysisService, PythonAnalysisService>();
        builder.Services.AddSingleton<IBeatGridSerializer, BeatGridSerializer>();
        builder.Services.AddSingleton<ISeratoTagService, SeratoTagService>();
        builder.Services.AddSingleton<JsonExportService>();
        builder.Services.AddSingleton(AudioManager.Current);
        builder.Services.AddSingleton<IAudioPlaybackService, AudioPlaybackService>();

        // ViewModels
        builder.Services.AddSingleton<PlaybackViewModel>();
        builder.Services.AddSingleton<MainViewModel>();

        // Pages
        builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
