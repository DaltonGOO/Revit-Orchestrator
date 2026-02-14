using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitOrchestrator.Models;

namespace RevitOrchestrator.Execution;

/// <summary>
/// Helper that wraps Revit API calls in a transaction and tracks model changes.
/// </summary>
public static class TransactionWrapper
{
    /// <summary>
    /// Execute an action within a Revit transaction, tracking model changes.
    /// </summary>
    public static ToolResult Execute(
        Document doc,
        string transactionName,
        Func<Document, ToolResult> action)
    {
        using var transaction = new Transaction(doc, transactionName);

        try
        {
            // Capture element IDs before transaction
            var beforeIds = GetAllElementIds(doc);

            transaction.Start();
            var result = action(doc);

            if (result.Success)
            {
                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                {
                    return ToolResult.Fail(
                        result.CallId,
                        "REVIT_TRANSACTION_FAILED",
                        $"Transaction commit returned status: {status}");
                }

                // Capture element IDs after transaction and compute changes
                var afterIds = GetAllElementIds(doc);
                var modelChanges = ComputeModelChanges(doc, beforeIds, afterIds);

                // Add model_changes to result data
                if (modelChanges.HasChanges)
                {
                    result = result.WithModelChanges(modelChanges);
                }
            }
            else
            {
                transaction.RollBack();
            }

            return result;
        }
        catch (Exception ex)
        {
            if (transaction.HasStarted())
                transaction.RollBack();

            return ToolResult.Fail("", "REVIT_API_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Snapshot all readable parameters of an element as a name→value dictionary.
    /// </summary>
    public static Dictionary<string, object?> SnapshotParameters(Element element)
    {
        var snapshot = new Dictionary<string, object?>();
        foreach (Parameter param in element.Parameters)
        {
            if (param.Definition == null) continue;
            var name = param.Definition.Name;
            snapshot[name] = GetParameterValue(param);
        }
        return snapshot;
    }

    /// <summary>
    /// Diff two parameter snapshots and return a list of changes.
    /// </summary>
    public static List<ParameterChange> DiffParameters(
        Dictionary<string, object?> before,
        Dictionary<string, object?> after)
    {
        var changes = new List<ParameterChange>();

        foreach (var kvp in after)
        {
            before.TryGetValue(kvp.Key, out var oldValue);
            if (!Equals(oldValue, kvp.Value))
            {
                changes.Add(new ParameterChange
                {
                    ParameterName = kvp.Key,
                    OldValue = oldValue,
                    NewValue = kvp.Value,
                });
            }
        }

        return changes;
    }

    /// <summary>
    /// Get all element IDs in the document (excluding types and system elements).
    /// </summary>
    private static HashSet<long> GetAllElementIds(Document doc)
    {
        return new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .ToElementIds()
            .Select(id => (long)id.Value)
            .ToHashSet();
    }

    /// <summary>
    /// Compute the differences between before and after element ID sets.
    /// </summary>
    private static ModelChanges ComputeModelChanges(
        Document doc,
        HashSet<long> beforeIds,
        HashSet<long> afterIds)
    {
        var changes = new ModelChanges();

        // Find created elements (in after but not in before)
        var createdIds = afterIds.Except(beforeIds);
        foreach (var id in createdIds)
        {
#if REVIT2025 || REVIT2026
            var element = doc.GetElement(new ElementId(id));
#else
            var element = doc.GetElement(new ElementId((int)id));
#endif
            if (element != null)
            {
                changes.Created.Add(GetElementInfo(element));
            }
        }

        // Find deleted elements (in before but not in after)
        var deletedIds = beforeIds.Except(afterIds);
        foreach (var id in deletedIds)
        {
            changes.Deleted.Add(id);
        }

        return changes;
    }

    /// <summary>
    /// Extract information about an element for change tracking.
    /// </summary>
    private static ElementChange GetElementInfo(Element element)
    {
        var typeId = element.GetTypeId();
        ElementType? elementType = null;
        string? familyName = null;

        if (typeId != ElementId.InvalidElementId)
        {
            elementType = element.Document.GetElement(typeId) as ElementType;
            if (elementType is FamilySymbol fs)
            {
                familyName = fs.FamilyName;
            }
        }

        var change = new ElementChange
        {
            ElementId = element.Id.Value,
            UniqueId = element.UniqueId,
            Category = element.Category?.Name,
            TypeName = elementType?.Name ?? element.GetType().Name,
            FamilyName = familyName,
            Name = element.Name,
        };

        // Extract bounding box
        try
        {
            var bbox = element.get_BoundingBox(null);
            if (bbox != null)
            {
                change.BboxMin = new[] { bbox.Min.X, bbox.Min.Y, bbox.Min.Z };
                change.BboxMax = new[] { bbox.Max.X, bbox.Max.Y, bbox.Max.Z };
            }
        }
        catch
        {
            // Some elements may not have a bounding box
        }

        return change;
    }

    /// <summary>
    /// Extract a parameter value as a boxed object.
    /// </summary>
    private static object? GetParameterValue(Parameter param)
    {
        if (!param.HasValue) return null;

        return param.StorageType switch
        {
            StorageType.Integer => param.AsInteger(),
            StorageType.Double => param.AsDouble(),
            StorageType.String => param.AsString(),
            StorageType.ElementId => param.AsElementId()?.Value,
            _ => null,
        };
    }
}
