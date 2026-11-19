namespace MinabClassManager.Models;

public class StudentEntry
{
    public string Name { get; set; } = string.Empty;
    public string ClassTitle { get; set; } = string.Empty;
    public bool UseCamera { get; set; }
    public bool UseMic { get; set; }
    public StudentWindow? Window { get; set; }

    public string CameraDisplay => UseCamera ? "✓ Fake" : "✗ None";
    public string MicDisplay => UseMic ? "✓ Fake" : "✗ None";
}
