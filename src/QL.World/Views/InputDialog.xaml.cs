using System.Windows;

namespace QL.World.Views;

/// <summary>
/// W3 generic single-line input dialog. Caller passes title/prompt/initial
/// value; reads <see cref="InputText"/> after <see cref="ShowDialog"/> returns true.
/// </summary>
public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initialValue ?? string.Empty;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>Result text read by caller when dialog returns true.</summary>
    public string InputText => InputBox.Text;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
