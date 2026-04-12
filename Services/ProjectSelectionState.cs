using System;

namespace DV.Web.Services;

public class ProjectSelectionState
{
    public int? SelectedProjectId { get; private set; }
    public string? SelectedProjectName { get; private set; }
    public string? SelectedSchemaName { get; private set; }
    public string? EditPrincipal { get; private set; }
    public bool HasEditAccess { get; private set; }

    public event Action? OnChange;

    public void SetProject(int? projectId, string? projectName, string? editPrincipal, string? schemaName = null, bool hasEditAccess = false)
    {
        SelectedProjectId = projectId;
        SelectedProjectName = projectName;
        SelectedSchemaName = schemaName;
        EditPrincipal = editPrincipal;
        HasEditAccess = hasEditAccess;
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => OnChange?.Invoke();
}
