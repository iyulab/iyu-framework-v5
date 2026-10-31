namespace Iyu.Uncad;

/// <summary>
/// What <c>uncad summarize</c> read from one drawing file (DWG or DXF): its units, entity and layer counts, the dimension
/// entries, and the values that sit next to a label that looks like a drawing number.
/// </summary>
/// <remarks>
/// It is what the program read, not an interpretation of it: a value the program did not report is <c>null</c>, never a default,
/// and a number-like label with a value beside it is a candidate, not a confirmed drawing number.
/// </remarks>
/// <param name="Units">The drawing's unit name (for example <c>mm</c>); <c>null</c> when it is not stated.</param>
/// <param name="EntityCount">The number of entities in the drawing.</param>
/// <param name="Confidence">The program's own confidence label for the reading (<c>UNKNOWN</c> when it gives none).</param>
/// <param name="Warnings">
/// The warnings the program reported about parts of the file it could not decode (<c>UNHANDLEDCLASS</c>, for one). An entity it
/// could not decode is missing from the other lists.
/// </param>
/// <param name="Layers">The layers, with the number of entities on each.</param>
/// <param name="Dimensions">The dimension entries read from the drawing.</param>
/// <param name="NumberCandidates">Label and value pairs whose label looks like a drawing-number label, in order, without repeats.</param>
public sealed record DrawingProbe(
    string? Units,
    int EntityCount,
    string Confidence,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ProbeLayer> Layers,
    IReadOnlyList<ProbeDimension> Dimensions,
    IReadOnlyList<DrawingNumberCandidate> NumberCandidates);

/// <summary>One layer of a drawing.</summary>
/// <param name="Name">The layer's name.</param>
/// <param name="ColorIndex">The layer's color as an AutoCAD color index (ACI); <c>null</c> when none is reported.</param>
/// <param name="EntityCount">The number of entities on the layer.</param>
public sealed record ProbeLayer(string Name, int? ColorIndex, int EntityCount);

/// <summary>One dimension entry of a drawing.</summary>
/// <param name="Id">The entity's id within the drawing.</param>
/// <param name="Kind">The kind of dimension as the program names it (<c>ROTATED</c>, <c>DIAMETER</c>, ...); <c>UNKNOWN</c> when absent.</param>
/// <param name="Measurement">The measured value; <c>null</c> when the program reports none.</param>
/// <param name="Text">
/// The text someone typed in place of the measured value (for example <c>"BOLT CIRCLE ⌀&lt;&gt;"</c>, where <c>&lt;&gt;</c> stands for
/// the measured value); <c>null</c> when the dimension shows its measured value as it is.
/// </param>
/// <param name="Tolerance">The tolerance the drawing itself states for this dimension; <c>null</c> when it states none.</param>
/// <param name="Layer">
/// The name of the layer the dimension is on. Only filled when the marks were read (<c>includeMarks</c>), and only for a
/// dimension the second run reported.
/// </param>
/// <param name="ColorIndex">
/// The effective AutoCAD color index: the entity's own color if it has one, the layer's color if it follows its layer. <c>null</c>
/// when it follows its block (the color is not knowable here) or is a true color. Only filled when the marks were read.
/// </param>
/// <param name="TrueColor">Whether the entity carries a true color (24-bit RGB) instead of an index. Only meaningful when the marks were read.</param>
public sealed record ProbeDimension(
    long Id,
    string Kind,
    double? Measurement,
    string? Text,
    StatedTolerance? Tolerance,
    string? Layer,
    int? ColorIndex,
    bool TrueColor);

/// <summary>
/// A tolerance the drawing states itself, read from stacked text such as <c>+0.022</c> over <c>+0.000</c>.
/// </summary>
/// <remarks>
/// The program's <c>tolerance_overrides</c> entries are not tolerances. The variable they commonly carry (272) is the number of
/// decimal places a tolerance is shown with, a formatting setting, so a dimension can carry it without stating any tolerance;
/// such entries are never reported here.
/// </remarks>
/// <param name="Upper">The upper value as written.</param>
/// <param name="Lower">The lower value as written.</param>
/// <param name="UpperValue">The upper value as a number; <c>null</c> when the text is not a plain number.</param>
/// <param name="LowerValue">The lower value as a number; <c>null</c> when the text is not a plain number.</param>
/// <param name="Source">
/// <c>stack</c> when read from stacked text, or <c>style</c> when read from the dimension style's tolerance settings (the values
/// are the style's upper and lower deviations exactly as stored; the sign convention of the lower value is not interpreted, so a
/// reader should treat a <c>style</c> tolerance as something to confirm).
/// </param>
public sealed record StatedTolerance(string Upper, string Lower, decimal? UpperValue, decimal? LowerValue, string Source);

/// <summary>A label and the value beside it, where the label looks like a drawing-number label (for example <c>DWG. NO.</c>).</summary>
/// <param name="Label">The label text.</param>
/// <param name="Value">The value text.</param>
public sealed record DrawingNumberCandidate(string Label, string Value);
