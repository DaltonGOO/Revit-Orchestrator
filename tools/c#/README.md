# Revit Orchestrator C# tools

A C# tool is **one `.cs` file** with no class boilerplate. The orchestrator
compiles it with Roslyn and runs it on the Revit API thread. Same idea as
the pyRevit Python tool convention; different language, same shape.

## Minimal example

```csharp
// tools/c#/wall_count.cs
var walls = new FilteredElementCollector(doc)
    .OfClass(typeof(Wall))
    .Cast<Wall>()
    .ToList();

return new Dictionary<string, object>
{
    ["wall_count"] = walls.Count,
};
```

That's the whole tool. No `using`s required (we pre-import the common
namespaces). No class wrapper. No `public static Run(...)`.

## What's in scope

| Global    | Type                            | What it is                              |
|-----------|---------------------------------|-----------------------------------------|
| `uiapp`   | `UIApplication`                 | The running Revit UI application.       |
| `doc`     | `Document`                      | The active document.                    |
| `inputs`  | `Dictionary<string, object?>`   | Arguments the LLM passed in.            |

## Pre-imported

```
System
System.Collections.Generic
System.Linq
System.IO
Autodesk.Revit.DB
Autodesk.Revit.UI
```

Pre-referenced assemblies include `RevitAPI`, `RevitAPIUI`, `System.Linq`,
`System.IO`, and `System.Text.Json`. If you need something else, add a
`#r "Some.Assembly.dll"` directive at the top of the script.

## What to return

A `Dictionary<string, object>` (or `Dictionary<string, object?>`). Goes to
the chat as JSON.

* **Success:** any dict shape. Common keys are `result`, `count`,
  `created`, `modified`, plus whatever the caller might find useful.
* **Failure:** include an `"error"` key. Add context — `"available": [...]`,
  `"hint": "..."` — so the LLM can fix the input and retry without a
  separate round trip.

If you return something other than a dict, it's wrapped as
`{"result": <value>}` so the shape is always consistent. Revit `Element`s
are summarised to `{element_id, name, category, type_name}` automatically.

## Transactions

The host doesn't open a transaction for you — your script manages its own
if it needs to mutate the model:

```csharp
using (var t = new Transaction(doc, "Add wall"))
{
    t.Start();
    // ... API mutations ...
    t.Commit();
}
```

Element-creation/deletion is auto-tracked: anything new or gone after the
script returns shows up in the chat's "model changes" panel without
explicit reporting.

## Examples

### Read-only query
```csharp
var schedules = new FilteredElementCollector(doc)
    .OfClass(typeof(ViewSchedule))
    .Cast<ViewSchedule>()
    .Where(s => !s.IsTemplate)
    .Select(s => s.Name)
    .OrderBy(n => n)
    .ToList();

return new Dictionary<string, object> { ["schedules"] = schedules };
```

### Mutation with transaction
```csharp
var name = (string)inputs["sheet_name"];

using (var t = new Transaction(doc, "Create sheet"))
{
    t.Start();
    var titleBlock = new FilteredElementCollector(doc)
        .OfCategory(BuiltInCategory.OST_TitleBlocks)
        .WhereElementIsElementType()
        .First()
        .Id;
    var sheet = ViewSheet.Create(doc, titleBlock);
    sheet.Name = name;
    t.Commit();

    return new Dictionary<string, object>
    {
        ["sheet_id"] = (long)sheet.Id.Value,
        ["sheet_name"] = sheet.Name,
        ["sheet_number"] = sheet.SheetNumber,
    };
}
```

### Returning a soft error
```csharp
if (!inputs.ContainsKey("level_name"))
{
    return new Dictionary<string, object>
    {
        ["error"] = "level_name is required",
        ["available_levels"] = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .Select(l => l.Name)
            .ToList(),
    };
}
```

## Errors

The chat shows whichever stage failed:

1. **Compile error** — your script doesn't compile. The error message lists
   the Roslyn diagnostics with line numbers.
2. **Runtime exception** — your script threw. The exception type, message,
   and a short stack are reported.
3. **`{"error": "..."}` returned** — soft failure with the rest of the dict
   inlined as context.

## When to use C# vs Python vs Dynamo

* **C# (this folder)** — fast, type-safe, full Revit API. Best for read-only
  queries and mutations where you'd reach for the API directly.
* **pyRevit Python (`tools/pyrevit/`)** — looser typing, easier to iterate,
  full pyRevit ecosystem available. Best when you want to copy/paste from
  existing pyRevit scripts.
* **Dynamo (`tools/dynamo/`)** — visual graph, great for designers who
  built it that way already. Best when the logic already exists as a `.dyn`.
