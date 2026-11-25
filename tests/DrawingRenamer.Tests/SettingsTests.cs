namespace DrawingRenamer.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("drawing-settings-").FullName;
    private string SettingsFile => Path.Combine(folder, "settings.json");

    [Fact]
    public async Task SaveRestoresAllSettingsInANewSession()
    {
        var templatePath = Path.Combine(folder, "templates.json");
        (TitleTemplate.Defaults[0] with { Name = "Saved template" }).Save(templatePath, newFile: true);
        var first = new MainViewModel(SettingsFile) { ThemeIndex = 2, SeparatorInput = " - ", OcrEnabled = false, Languages = "eng" };
        first.SetTemplates(templatePath);
        first.SetScannerFolder(folder);
        first.ScannerWatchEnabled = true;
        first.ScannerCreatedAfter = DateTimeOffset.Now.AddHours(-3);
        first.SplitScanBatches = true;
        await first.SaveSettingsAsync();
        Assert.True(File.Exists(SettingsFile));

        var restored = new MainViewModel(SettingsFile);
        await restored.LoadSettingsAsync();
        Assert.Equal(2, restored.ThemeIndex);
        Assert.Equal(" - ", restored.SeparatorInput);
        Assert.False(restored.OcrEnabled);
        Assert.Equal("eng", restored.Languages);
        Assert.Equal(2, restored.LanguageIndex);
        Assert.Equal(templatePath, restored.TemplatePath);
        Assert.Equal(folder, restored.ScannerFolderLabel);
        Assert.True(restored.ScannerWatchEnabled);
        Assert.Equal(first.ScannerCreatedAfter, restored.ScannerCreatedAfter);
        Assert.True(restored.SplitScanBatches);
        Assert.Contains("Saved template", (await restored.LoadRecognitionOptionsAsync()).Templates!.Select(t => t.Name));
    }

    [Fact]
    public async Task EditsAreNotPersistedUntilSaveIsPressed()
    {
        var first = new MainViewModel(SettingsFile) { SeparatorInput = "   ", Languages = "rus+eng" };
        await first.SaveSettingsAsync();
        first.SeparatorInput = "#";
        first.Languages = "eng";
        var restored = new MainViewModel(SettingsFile);
        await restored.LoadSettingsAsync();
        Assert.Equal("   ", restored.SeparatorInput);
        Assert.Equal("rus+eng", restored.Languages);
    }

    [Fact]
    public async Task InvalidSeparatorCannotOverwriteSavedSettings()
    {
        var model = new MainViewModel(SettingsFile) { SeparatorInput = "#" };
        await model.SaveSettingsAsync();
        var saved = File.ReadAllBytes(SettingsFile);
        model.SeparatorInput = "_уу";
        await model.SaveSettingsAsync();
        Assert.Equal(saved, File.ReadAllBytes(SettingsFile));
        Assert.Equal(model.SeparatorError, model.SettingsMessage);
    }

    [Fact]
    public async Task ResetClearsInvalidInputAndPersistsEveryDefault()
    {
        var templatePath = Path.Combine(folder, "templates.json");
        TitleTemplate.Defaults[0].Save(templatePath, newFile: true);
        var model = new MainViewModel(SettingsFile) { ThemeIndex = 1, SeparatorInput = "#", OcrEnabled = false, Languages = "rus+eng" };
        model.SetTemplates(templatePath);
        model.SetScannerFolder(folder);
        model.ScannerWatchEnabled = true;
        await model.SaveSettingsAsync();
        model.SeparatorInput = "_уу";
        await model.ResetSettingsAsync();
        AssertDefaults(model);
        Assert.True(File.Exists(templatePath));
        var restored = new MainViewModel(SettingsFile);
        await restored.LoadSettingsAsync();
        AssertDefaults(restored);
    }

    [Fact]
    public async Task CorruptSettingsUseDefaultsAndReportTheProblem()
    {
        File.WriteAllText(SettingsFile, "{broken json");
        var model = new MainViewModel(SettingsFile);
        await model.LoadSettingsAsync();
        AssertDefaults(model);
        Assert.Contains("по умолчанию", model.SettingsMessage);
    }

    [Fact]
    public async Task MissingCustomTemplateDoesNotDiscardOtherSavedSettings()
    {
        var templatePath = Path.Combine(folder, "templates.json");
        TitleTemplate.Defaults[0].Save(templatePath, newFile: true);
        var model = new MainViewModel(SettingsFile) { SeparatorInput = " - ", ThemeIndex = 2 };
        model.SetTemplates(templatePath);
        await model.SaveSettingsAsync();
        File.Delete(templatePath);
        var restored = new MainViewModel(SettingsFile);
        await restored.LoadSettingsAsync();
        Assert.Equal(" - ", restored.SeparatorInput);
        Assert.Equal(2, restored.ThemeIndex);
        Assert.Null(restored.TemplatePath);
        Assert.Contains("шаблон", restored.SettingsMessage);
    }

    [Fact]
    public async Task SaveFailureIsVisibleAndDoesNotReportSuccess()
    {
        var model = new MainViewModel(folder) { SeparatorInput = "#" };
        await model.SaveSettingsAsync();
        Assert.Contains("Не удалось сохранить", model.SettingsMessage);
        Assert.False(model.Busy);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task InvalidSavedValuesUseSafeDefaultsWithoutDiscardingValidOptions()
    {
        File.WriteAllText(SettingsFile, "{\"ThemeIndex\":99,\"Separator\":\"_уу\",\"Languages\":\"unknown\",\"OcrEnabled\":false}");
        var model = new MainViewModel(SettingsFile);
        await model.LoadSettingsAsync();
        Assert.Equal(0, model.ThemeIndex);
        Assert.Equal("_", model.SeparatorInput);
        Assert.Equal("rus", model.Languages);
        Assert.False(model.OcrEnabled);
    }

    [Fact]
    public async Task MissingSettingsFileStartsWithDefaults()
    {
        var model = new MainViewModel(SettingsFile);
        await model.LoadSettingsAsync();
        AssertDefaults(model);
        Assert.False(File.Exists(SettingsFile));
    }

    private static void AssertDefaults(MainViewModel model)
    {
        Assert.Equal(0, model.ThemeIndex);
        Assert.Equal("_", model.SeparatorInput);
        Assert.Null(model.SeparatorError);
        Assert.True(model.OcrEnabled);
        Assert.Equal("rus", model.Languages);
        Assert.Equal(0, model.LanguageIndex);
        Assert.Null(model.TemplatePath);
        Assert.Equal("Встроенные шаблоны ЕСКД", model.TemplateLabel);
        Assert.False(model.HasScannerFolder);
        Assert.False(model.ScannerWatchEnabled);
    }

    public void Dispose() => Directory.Delete(folder, true);
}
