using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;

namespace BetterInspector;

/// <summary>One error shown in an Error Layer.</summary>
internal sealed class ErrorInspectionItem
{
    public required string ErrorLayer { get; init; }
    public required long ObjectId { get; init; }
    public required string Rule { get; init; }
    public required string RuleType { get; init; }
    public required string ErrorNumber { get; init; }
    public required string Message { get; init; }
    public required string ExceptionStatus { get; init; }
    public required string FeatureClass { get; init; }
    public required string AssetGroup { get; init; }
    public required string FeatureObjectId { get; init; }
    public required string FeatureGlobalId { get; init; }
    public required string Description { get; init; }
    public required string Severity { get; init; }
    public required string ShapeIndicator { get; init; }
    public required long SourceClassId { get; init; }
    public required long SourceObjectId { get; init; }
    public required ValidationErrorType ErrorType { get; init; }
    public MapMember? SourceMapMember { get; init; }
    public Geometry? Geometry { get; init; }
}
