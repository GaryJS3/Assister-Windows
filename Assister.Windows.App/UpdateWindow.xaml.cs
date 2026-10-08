using System.Windows;

namespace Assister.Windows.App;

public partial class UpdateWindow : Window
{
    private bool _canClose;

    public UpdateWindow()
    {
        InitializeComponent();
        Closing += (_, e) => e.Cancel = !_canClose;
    }

    internal void Report(string status) => StatusLabel.Text = status;

    internal void ShowFailure(string message)
    {
        _canClose = true;
        StatusLabel.Text = "The update could not finish.";
        DetailLabel.Text = message;
        UpdateProgress.IsIndeterminate = false;
        CloseButton.Visibility = Visibility.Visible;
        SizeToContent = SizeToContent.Height;
    }

    internal void Complete()
    {
        _canClose = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
