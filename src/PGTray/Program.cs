namespace PGTray;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        WindowsSessionShutdown.SetPriority();
        // Keep the v0.1 coordination names: old PGTray and new PgHatch must not run together.
        // A rebrand must not bypass the legacy manager's exclusion or restore-window event.
        using var mutex = new Mutex(true, "Local\\PGTray-Manager", out var first);
        using var showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\PGTray-ShowWindow");
        if (!first) { showWindow.Set(); return; }
        var systemLanguage = System.Globalization.CultureInfo.CurrentUICulture;
        var preferences = new UiPreferences(AppContext.BaseDirectory);
        // No window exists yet. File work runs off the UI thread; Main remains STA.
        Task.Run(preferences.LoadAsync).GetAwaiter().GetResult();
        var culture = System.Globalization.CultureInfo.GetCultureInfo(UiPreferences.ResolveLanguage(preferences.Language, systemLanguage));
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        System.Globalization.CultureInfo.CurrentUICulture = culture;
        Application.Run(new MainForm(showWindow, preferences));
    }    
}
