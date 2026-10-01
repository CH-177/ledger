using Microsoft.Windows.Storage.Pickers;

namespace Ledger.App.Services;

/// <summary>
/// Windows save/open dialogs, using the Windows App SDK pickers made for unpackaged apps (the older
/// Windows.Storage.Pickers route through a broker process that can fail to show a window).
/// </summary>
public sealed class FilePickerService(Func<MainWindow> window)
{
    /// <summary>Asks where to save. Returns the chosen path, or null if cancelled.</summary>
    public async Task<string?> PickSaveAsync(string suggestedName, string typeLabel, string extension)
    {
        var picker = new FileSavePicker(window().AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
            DefaultFileExtension = extension,
        };
        picker.FileTypeChoices.Add(typeLabel, [extension]);
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }

    /// <summary>Asks for a file to open (any of the given extensions). Returns the chosen path, or null if cancelled.</summary>
    public async Task<string?> PickOpenAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker(window().AppWindow.Id) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }
        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }
}
