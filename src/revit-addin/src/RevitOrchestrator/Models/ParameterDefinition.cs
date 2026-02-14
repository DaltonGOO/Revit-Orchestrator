using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RevitOrchestrator.Models;

/// <summary>
/// Represents a single parameter in a tool definition, used by the Tool Editor dialog.
/// </summary>
public sealed class ParameterDefinition : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _type = "string";
    private string _description = string.Empty;
    private string? _defaultValue;
    private bool _isRequired;
    private double? _minimum;
    private double? _maximum;
    private string? _enumValues;
    private int? _minItems;
    private int? _maxItems;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>JSON Schema type: string, integer, number, boolean, array, object.</summary>
    public string Type
    {
        get => _type;
        set
        {
            _type = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNumericType));
            OnPropertyChanged(nameof(IsStringType));
            OnPropertyChanged(nameof(IsArrayType));
        }
    }

    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged(); }
    }

    /// <summary>Default value serialized as a string.</summary>
    public string? DefaultValue
    {
        get => _defaultValue;
        set { _defaultValue = value; OnPropertyChanged(); }
    }

    public bool IsRequired
    {
        get => _isRequired;
        set { _isRequired = value; OnPropertyChanged(); }
    }

    // Constraints
    public double? Minimum
    {
        get => _minimum;
        set { _minimum = value; OnPropertyChanged(); }
    }

    public double? Maximum
    {
        get => _maximum;
        set { _maximum = value; OnPropertyChanged(); }
    }

    /// <summary>Comma-separated enum values for string type.</summary>
    public string? EnumValues
    {
        get => _enumValues;
        set { _enumValues = value; OnPropertyChanged(); }
    }

    /// <summary>Minimum items for array type.</summary>
    public int? MinItems
    {
        get => _minItems;
        set { _minItems = value; OnPropertyChanged(); }
    }

    /// <summary>Maximum items for array type.</summary>
    public int? MaxItems
    {
        get => _maxItems;
        set { _maxItems = value; OnPropertyChanged(); }
    }

    // Computed helpers for UI visibility
    public bool IsNumericType => _type is "number" or "integer";
    public bool IsStringType => _type == "string";
    public bool IsArrayType => _type == "array";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
