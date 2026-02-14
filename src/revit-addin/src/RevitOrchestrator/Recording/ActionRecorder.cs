using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Recording;

/// <summary>
/// Records user actions in Revit by hooking into document and command events.
/// </summary>
public sealed class ActionRecorder : IDisposable
{
    private readonly UIApplication _uiApp;
    private readonly List<RecordedAction> _actions = new();
    private readonly object _lock = new();
    private bool _isRecording;
    private string? _lastCommandName;
    private DateTime _lastCommandTime;

    /// <summary>
    /// Event raised when a new action is recorded.
    /// </summary>
    public event Action<RecordedAction>? ActionRecorded;

    /// <summary>
    /// Event raised when recording state changes.
    /// </summary>
    public event Action<bool>? RecordingStateChanged;

    public ActionRecorder(UIApplication uiApp)
    {
        _uiApp = uiApp;
    }

    /// <summary>
    /// Whether recording is currently active.
    /// </summary>
    public bool IsRecording => _isRecording;

    /// <summary>
    /// All recorded actions.
    /// </summary>
    public IReadOnlyList<RecordedAction> Actions
    {
        get
        {
            lock (_lock)
            {
                return _actions.ToList();
            }
        }
    }

    /// <summary>
    /// Start recording user actions.
    /// </summary>
    public void StartRecording()
    {
        if (_isRecording) return;

        _isRecording = true;

        // Hook into document changed events (tracks model changes)
        _uiApp.Application.DocumentChanged += OnDocumentChanged;

        // Hook into command events (tracks UI commands)
        _uiApp.Application.DocumentOpened += OnDocumentOpened;
        _uiApp.Application.DocumentClosed += OnDocumentClosed;

        // Try to hook into command execution if available
        try
        {
            // These events track when Revit commands are executed
            _uiApp.Application.FailuresProcessing += OnFailuresProcessing;
        }
        catch
        {
            // Some events may not be available in all Revit versions
        }

        RecordingStateChanged?.Invoke(true);
    }

    /// <summary>
    /// Stop recording user actions.
    /// </summary>
    public void StopRecording()
    {
        if (!_isRecording) return;

        _isRecording = false;

        // Unhook all events
        _uiApp.Application.DocumentChanged -= OnDocumentChanged;
        _uiApp.Application.DocumentOpened -= OnDocumentOpened;
        _uiApp.Application.DocumentClosed -= OnDocumentClosed;

        try
        {
            _uiApp.Application.FailuresProcessing -= OnFailuresProcessing;
        }
        catch { }

        RecordingStateChanged?.Invoke(false);
    }

    /// <summary>
    /// Clear all recorded actions.
    /// </summary>
    public void ClearActions()
    {
        lock (_lock)
        {
            _actions.Clear();
        }
    }

    /// <summary>
    /// Handles document changed events - this is the main source of model change tracking.
    /// </summary>
    private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
    {
        try
        {
            if (!_isRecording) return;

            var doc = e.GetDocument();
            var addedIds = e.GetAddedElementIds();
            var modifiedIds = e.GetModifiedElementIds();
            var deletedIds = e.GetDeletedElementIds();

            // Skip if no changes
            if (addedIds.Count == 0 && modifiedIds.Count == 0 && deletedIds.Count == 0)
                return;

            // Skip internal/transient changes
            var transactionNames = e.GetTransactionNames();
            if (transactionNames.Any(n => n.StartsWith("Orchestrator:") || n.Contains("Regen")))
                return;

            // Determine the action type and category
            var actionType = DetermineActionType(addedIds, modifiedIds, deletedIds);
            var category = DetermineCategory(doc, addedIds, modifiedIds);
            var description = BuildDescription(doc, actionType, addedIds, modifiedIds, deletedIds, transactionNames);

            var action = new RecordedAction
            {
                Timestamp = DateTime.Now,
                ActionType = actionType,
                CommandName = _lastCommandName,
                Description = description,
                Category = category,
                CreatedElementIds = addedIds.Select(id => (long)id.Value).ToList(),
                ModifiedElementIds = modifiedIds.Select(id => (long)id.Value).ToList(),
                DeletedElementIds = deletedIds.Select(id => (long)id.Value).ToList(),
                MappedToolName = MapToToolName(actionType, category),
            };

            // Try to capture parameters from created elements
            if (addedIds.Count > 0)
            {
                CaptureElementParameters(doc, addedIds.First(), action);
            }

            RecordAction(action);

            // Clear last command after it's been associated with an action
            _lastCommandName = null;
        }
        catch (Exception)
        {
            // Swallow exceptions in event handlers to prevent crashing Revit
        }
    }

    /// <summary>
    /// Track document open events.
    /// </summary>
    private void OnDocumentOpened(object sender, DocumentOpenedEventArgs e)
    {
        try
        {
            if (!_isRecording) return;

            var action = new RecordedAction
            {
                Timestamp = DateTime.Now,
                ActionType = RecordedActionType.Command,
                CommandName = "DocumentOpen",
                Description = $"Opened document: {e.Document.Title}",
            };

            RecordAction(action);
        }
        catch (Exception)
        {
            // Swallow exceptions to prevent crashing Revit
        }
    }

    /// <summary>
    /// Track document close events.
    /// </summary>
    private void OnDocumentClosed(object sender, DocumentClosedEventArgs e)
    {
        try
        {
            if (!_isRecording) return;

            var action = new RecordedAction
            {
                Timestamp = DateTime.Now,
                ActionType = RecordedActionType.Command,
                CommandName = "DocumentClose",
                Description = "Closed document",
            };

            RecordAction(action);
        }
        catch (Exception)
        {
            // Swallow exceptions to prevent crashing Revit
        }
    }

    /// <summary>
    /// Track failures which can indicate command execution context.
    /// </summary>
    private void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
    {
        // Can use this to detect command context
    }

    /// <summary>
    /// Record a command being executed (called from external hook if available).
    /// </summary>
    public void RecordCommand(string commandName)
    {
        _lastCommandName = commandName;
        _lastCommandTime = DateTime.Now;
    }

    private void RecordAction(RecordedAction action)
    {
        lock (_lock)
        {
            _actions.Add(action);
        }

        ActionRecorded?.Invoke(action);
    }

    private static RecordedActionType DetermineActionType(
        ICollection<ElementId> added,
        ICollection<ElementId> modified,
        ICollection<ElementId> deleted)
    {
        // Prioritize by significance
        if (deleted.Count > 0 && added.Count == 0 && modified.Count == 0)
            return RecordedActionType.Delete;
        if (added.Count > 0)
            return RecordedActionType.Create;
        if (modified.Count > 0)
            return RecordedActionType.Modify;
        return RecordedActionType.Unknown;
    }

    private static string? DetermineCategory(Document doc, ICollection<ElementId> added, ICollection<ElementId> modified)
    {
        var idsToCheck = added.Count > 0 ? added : modified;
        if (idsToCheck.Count == 0) return null;

        var element = doc.GetElement(idsToCheck.First());
        return element?.Category?.Name;
    }

    private static string BuildDescription(
        Document doc,
        RecordedActionType actionType,
        ICollection<ElementId> added,
        ICollection<ElementId> modified,
        ICollection<ElementId> deleted,
        IList<string> transactionNames)
    {
        // Use transaction name if meaningful
        var txName = transactionNames.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        if (!string.IsNullOrEmpty(txName) && !txName.StartsWith("_"))
        {
            return txName;
        }

        // Build description from action type and elements
        var verb = actionType switch
        {
            RecordedActionType.Create => "Created",
            RecordedActionType.Modify => "Modified",
            RecordedActionType.Delete => "Deleted",
            _ => "Changed"
        };

        var count = actionType switch
        {
            RecordedActionType.Create => added.Count,
            RecordedActionType.Modify => modified.Count,
            RecordedActionType.Delete => deleted.Count,
            _ => added.Count + modified.Count + deleted.Count
        };

        // Try to get category name
        var idsToCheck = added.Count > 0 ? added : modified;
        if (idsToCheck.Count > 0)
        {
            var element = doc.GetElement(idsToCheck.First());
            var category = element?.Category?.Name ?? "element";
            return $"{verb} {count} {category}{(count != 1 ? "s" : "")}";
        }

        return $"{verb} {count} element{(count != 1 ? "s" : "")}";
    }

    private static string? MapToToolName(RecordedActionType actionType, string? category)
    {
        if (string.IsNullOrEmpty(category)) return null;

        // Map common categories to tool names
        return category.ToLowerInvariant() switch
        {
            "walls" => actionType == RecordedActionType.Create ? "revit.create_wall" : "revit.modify_element",
            "floors" => actionType == RecordedActionType.Create ? "revit.create_floor" : "revit.modify_element",
            "roofs" => actionType == RecordedActionType.Create ? "revit.create_roof" : "revit.modify_element",
            "doors" => actionType == RecordedActionType.Create ? "revit.place_door" : "revit.modify_element",
            "windows" => actionType == RecordedActionType.Create ? "revit.place_window" : "revit.modify_element",
            "rooms" => actionType == RecordedActionType.Create ? "revit.create_room" : "revit.modify_element",
            "furniture" => actionType == RecordedActionType.Create ? "revit.place_family" : "revit.modify_element",
            // MEP categories → revit.create_element
            "ducts" or "duct accessories" or "duct fittings" or "flex ducts" => actionType == RecordedActionType.Create ? "revit.create_element" : "revit.modify_element",
            "pipes" or "pipe accessories" or "pipe fittings" or "flex pipes" => actionType == RecordedActionType.Create ? "revit.create_element" : "revit.modify_element",
            "cable trays" or "conduits" => actionType == RecordedActionType.Create ? "revit.create_element" : "revit.modify_element",
            _ => actionType == RecordedActionType.Create ? "revit.create_element" : "revit.modify_element"
        };
    }

    private static void CaptureElementParameters(Document doc, ElementId elementId, RecordedAction action)
    {
        var element = doc.GetElement(elementId);
        if (element == null) return;

        // Capture basic geometric info if available
        var location = element.Location;
        if (location is LocationPoint lp)
        {
            action.Parameters["location"] = new[] { lp.Point.X, lp.Point.Y, lp.Point.Z };
        }
        else if (location is LocationCurve lc)
        {
            var curve = lc.Curve;
            action.Parameters["start_point"] = new[] { curve.GetEndPoint(0).X, curve.GetEndPoint(0).Y, curve.GetEndPoint(0).Z };
            action.Parameters["end_point"] = new[] { curve.GetEndPoint(1).X, curve.GetEndPoint(1).Y, curve.GetEndPoint(1).Z };
        }

        // Capture level if available
        if (element.LevelId != ElementId.InvalidElementId)
        {
            var level = doc.GetElement(element.LevelId);
            if (level != null)
            {
                action.Parameters["level_name"] = level.Name;
            }
        }

        // Capture type name
        var typeId = element.GetTypeId();
        if (typeId != ElementId.InvalidElementId)
        {
            var type = doc.GetElement(typeId);
            if (type != null)
            {
                action.Parameters["type_name"] = type.Name;
            }
        }

        // Capture wall-specific parameters
        if (element is Wall wall)
        {
            var heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            if (heightParam?.HasValue == true)
            {
                action.Parameters["height"] = heightParam.AsDouble();
            }
        }

        // Capture MEP-specific parameters
        if (element is MEPCurve mepCurve)
        {
            var diamParam = mepCurve.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                         ?? mepCurve.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
            if (diamParam?.HasValue == true)
            {
                action.Parameters["diameter"] = diamParam.AsDouble();
            }

            // Record category hint so replay picks the right MEP type
            action.Parameters["category"] = element.Category?.Name ?? "";
        }
    }

    public void Dispose()
    {
        StopRecording();
    }
}
