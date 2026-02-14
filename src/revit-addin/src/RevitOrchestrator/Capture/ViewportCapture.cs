using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitOrchestrator.Capture;

/// <summary>
/// Captures the active Revit viewport as a PNG image and generates
/// screen-space overlay data for highlighted elements.
/// </summary>
public static class ViewportCapture
{
    /// <summary>
    /// Capture the active view as a PNG byte array.
    /// </summary>
    /// <param name="doc">The Revit document.</param>
    /// <param name="maxWidth">Maximum image width in pixels.</param>
    /// <returns>PNG bytes, or null if capture fails.</returns>
    public static byte[]? CaptureActiveView(Document doc, int maxWidth = 1024)
    {
        try
        {
            var view = doc.ActiveView;
            if (view == null) return null;

            var tempDir = Path.GetTempPath();
            var tempFile = Path.Combine(tempDir, $"RevitCapture_{System.Guid.NewGuid():N}.png");

            var options = new ImageExportOptions
            {
                FilePath = tempFile,
                FitDirection = FitDirectionType.Horizontal,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ExportRange = ExportRange.CurrentView,
            };

            // Set pixel size based on maxWidth
            options.PixelSize = maxWidth;

            doc.ExportImage(options);

            // ExportImage appends a suffix to the filename
            var actualFile = Directory.GetFiles(tempDir, Path.GetFileNameWithoutExtension(tempFile) + "*")
                .FirstOrDefault();

            if (actualFile == null || !File.Exists(actualFile))
                return null;

            var bytes = File.ReadAllBytes(actualFile);

            // Clean up temp file
            try { File.Delete(actualFile); } catch { }

            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Generate overlay data for highlighted elements in the current view.
    /// Returns JSON-serializable data with bounding box info for each element.
    /// </summary>
    /// <param name="doc">The Revit document.</param>
    /// <param name="view">The view to generate overlays for.</param>
    /// <param name="highlightIds">Element IDs to highlight.</param>
    /// <returns>List of overlay entries with element_id, bbox, category, and label.</returns>
    public static List<Dictionary<string, object?>> GenerateOverlay(
        Document doc,
        View view,
        IEnumerable<long> highlightIds)
    {
        var overlays = new List<Dictionary<string, object?>>();

        foreach (var eid in highlightIds)
        {
#if REVIT2025 || REVIT2026
            var element = doc.GetElement(new ElementId(eid));
#else
            var element = doc.GetElement(new ElementId((int)eid));
#endif
            if (element == null) continue;

            var bbox = element.get_BoundingBox(view);
            if (bbox == null) continue;

            overlays.Add(new Dictionary<string, object?>
            {
                ["element_id"] = eid,
                ["bbox_min"] = new[] { bbox.Min.X, bbox.Min.Y, bbox.Min.Z },
                ["bbox_max"] = new[] { bbox.Max.X, bbox.Max.Y, bbox.Max.Z },
                ["category"] = element.Category?.Name,
                ["label"] = element.Name,
            });
        }

        return overlays;
    }
}
