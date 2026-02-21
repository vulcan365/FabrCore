using System.Windows.Forms;

namespace FabrCore.Console.CliHost.Services;

public class FileDialogService
{
    public string? OpenFileDialog(string title, string filter = "JSON files (*.json)|*.json|All files (*.*)|*.*")
    {
        string? selectedPath = null;

        // OpenFileDialog requires an STA thread; console apps run on MTA by default
        var thread = new Thread(() =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = filter,
                FilterIndex = 1,
                RestoreDirectory = true
            };

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                selectedPath = dialog.FileName;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return selectedPath;
    }
}
