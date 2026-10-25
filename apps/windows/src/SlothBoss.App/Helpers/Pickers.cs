using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace SlothBoss.App.Helpers;

/// <summary>Native Windows file pickers, bound to the app window (required for unpackaged WinUI apps).</summary>
public static class Pickers
{
    private static void Bind(object picker, Window window) =>
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

    /// <summary>Shows a Save dialog. The system dialog itself asks before replacing an existing file.</summary>
    public static async Task<string?> SaveFileAsync(Window window, string suggestedName, string typeLabel, string extension)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add(typeLabel, [extension]);
        Bind(picker, window);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public static async Task<string?> PickFolderAsync(Window window)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads, ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add("*");
        Bind(picker, window);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public static async Task<string?> OpenFileAsync(Window window, params string[] extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
        foreach (var e in extensions) picker.FileTypeFilter.Add(e);
        Bind(picker, window);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
