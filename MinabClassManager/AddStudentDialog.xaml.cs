using System.Windows;
using System.Windows.Input;

namespace MinabClassManager;

public partial class AddStudentDialog : Window
{
    public string StudentName => nameBox.Text.Trim();
    public bool UseCamera => cameraCheck.IsChecked == true;
    public bool UseMic => micCheck.IsChecked == true;

    public AddStudentDialog(string classTitle, string suggestedName)
    {
        InitializeComponent();
        titleText.Text = $"Add student to \"{classTitle}\"";
        nameBox.Text = suggestedName;
        Loaded += (_, _) => { nameBox.SelectAll(); nameBox.Focus(); };
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(StudentName))
        {
            MessageBox.Show("Please enter a student name.", "Name required",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            nameBox.Focus();
            return;
        }
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}
