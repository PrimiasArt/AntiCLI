namespace AntiCliUi;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "AntiCLI Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        Application.Run(new MainForm());
    }
}
