using System;

namespace DV.Web.Services;

public class ProjectSelectionState
{
    public int? SelectedProjectId { get; private set; }
    public string? SelectedProjectName { get; private set; }
    public string? EditPrincipal { get; private set; }

    public event Action? OnChange;

    public void SetProject(int? projectId, string? projectName, string? editPrincipal)
    {
        SelectedProjectId = projectId;
        SelectedProjectName = projectName;
        EditPrincipal = editPrincipal;
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => OnChange?.Invoke();
}
