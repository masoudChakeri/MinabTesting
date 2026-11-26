using Microsoft.Win32;
using MinabClassManager.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace MinabClassManager;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ClassSession> _classes = new();
    private readonly ObservableCollection<StudentEntry> _students = new();
    private int _studentCounter = 0;

    public MainWindow()
    {
        InitializeComponent();
        classGrid.ItemsSource = _classes;
        studentGrid.ItemsSource = _students;
        UpdateStatus();
    }

    // ── Import ──────────────────────────────────────────────────────────────

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import Minab Classes",
            Filter = "Class files (*.json;*.jsonl)|*.json;*.jsonl|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var sessions = LoadClasses(dlg.FileName);
            _classes.Clear();
            foreach (var s in sessions)
                _classes.Add(s);

            fileInfoText.Text = $"·  {Path.GetFileName(dlg.FileName)}  ({sessions.Count} classes)";
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not load file:\n\n{ex.Message}",
                "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Add student ─────────────────────────────────────────────────────────

    private void AddStudentButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var session = (ClassSession)button.Tag;

        _studentCounter++;
        var dialog = new AddStudentDialog(session.Title, $"LT-{_studentCounter}") { Owner = this };

        if (dialog.ShowDialog() != true)
        {
            _studentCounter--;
            return;
        }

        var name = dialog.StudentName;
        var signedUrl = SignJoinName(session.JoinUrl, name);
        var studentWindow = new StudentWindow(signedUrl, dialog.UseCamera, dialog.UseMic, name, session.Title);

        var entry = new StudentEntry
        {
            Name = name,
            ClassTitle = session.Title,
            UseCamera = dialog.UseCamera,
            UseMic = dialog.UseMic,
            Window = studentWindow
        };

        studentWindow.Closed += (_, _) =>
        {
            _students.Remove(entry);
            UpdateStatus();
        };

        _students.Add(entry);
        studentWindow.Show();
        UpdateStatus();
    }

    // ── Remove student ───────────────────────────────────────────────────────

    private void RemoveStudentButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var entry = (StudentEntry)button.Tag;
        // Closing the window fires its Closed event which removes the entry
        try { entry.Window?.ForceClose(); }
        catch
        {
            _students.Remove(entry);
            UpdateStatus();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void ViewStudentButton_Click(object sender, RoutedEventArgs e)
    {
        var entry = (StudentEntry)((Button)sender).Tag;
        entry.Window?.ToggleView();
    }

    // Background student windows would otherwise keep the app alive
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    private void UpdateStatus() =>
        statusText.Text = $"{_classes.Count} classes  ·  {_students.Count} students this session";

    static List<ClassSession> LoadClasses(string filePath)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var content = File.ReadAllText(filePath);
        var trimmed = content.TrimStart();

        if (trimmed.StartsWith('['))
            return JsonSerializer.Deserialize<List<ClassSession>>(content, options) ?? [];

        // JSONL: one JSON object per line
        return content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith('{'))
            .Select(l => JsonSerializer.Deserialize<ClassSession>(l, options)!)
            .Where(c => c is not null)
            .ToList();
    }

    // Replicates ramp.js signJoinName: sha256(token + 0xFF + name)
    static string SignJoinName(string joinUrl, string name)
    {
        var uri = new Uri(joinUrl);
        var token = uri.Segments.Last().TrimEnd('/');

        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var data = new byte[tokenBytes.Length + 1 + nameBytes.Length];
        Buffer.BlockCopy(tokenBytes, 0, data, 0, tokenBytes.Length);
        data[tokenBytes.Length] = 0xFF;
        Buffer.BlockCopy(nameBytes, 0, data, tokenBytes.Length + 1, nameBytes.Length);

        using var sha = SHA256.Create();
        var sig = Convert.ToHexString(sha.ComputeHash(data)).ToLower();

        return $"{uri.GetLeftPart(UriPartial.Path)}?name={Uri.EscapeDataString(name)}&sig={sig}";
    }
}
