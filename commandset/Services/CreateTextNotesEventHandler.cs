using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Annotation;
using RevitMCPCommandSet.Services.AnnotationComponents;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services;

/// <summary>
/// Creates Annotate → Text notes (TextNote) on a floor plan, optionally with
/// leaders (TextNote.AddLeader) and simple detail lines. Types come from the
/// project only — never creates new TextNoteType.
/// </summary>
public class CreateTextNotesEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    public const string DefaultCommentTag = "MCP-ANN";
    /// <summary>Fallback when Comments is read-only on TextNote (some ADSK templates).</summary>
    private const string TextMarkerSuffix = "\u200B\u200B";
    public const string DefaultTextTypeName = "ADSK_Замечания";
    /// <summary>Margin beyond plan+grids to the text column (model mm).</summary>
    public const double DefaultOutsideMarginMm = 4000;
    /// <summary>Legacy near-element offset (model mm).</summary>
    public const double DefaultNearOffsetMm = 1500;
    /// <summary>Paper-space text box width (mm on sheet). Converted via view.Scale.</summary>
    public const double DefaultPaperWidthMm = 70;
    /// <summary>Desired TextNoteType height (paper mm). Applied to the type if different.</summary>
    public const double DefaultTextSizeMm = 2.5;
    /// <summary>Extra gap between stacked notes (paper mm → model via scale).</summary>
    public const double DefaultStackGapPaperMm = 2.0;
    public const string DefaultPlacement = "outside";

    private UIApplication _uiApp;
    private Document Doc => _uiApp.ActiveUIDocument.Document;

    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    private List<TextNotePlacementInfo> _notes = new();
    private List<DetailLinePlacementInfo> _lines = new();
    private bool _clearPrevious = true;
    private bool _clearOnly;
    private string _commentTag = DefaultCommentTag;
    private string _defaultTextTypeName = DefaultTextTypeName;
    private long _viewId = -1;
    private string _placement = DefaultPlacement;
    private double _marginMm = DefaultOutsideMarginMm;
    private double _paperWidthMm = DefaultPaperWidthMm;
    private double _textSizeMm = DefaultTextSizeMm;

    public object ResultInfo { get; private set; }

    public void SetParameters(
        IEnumerable<TextNotePlacementInfo> notes,
        IEnumerable<DetailLinePlacementInfo> lines,
        bool clearPrevious,
        string commentTag,
        string defaultTextTypeName,
        long viewId,
        string placement = DefaultPlacement,
        double marginMm = 0,
        double paperWidthMm = 0,
        double textSizeMm = 0,
        bool clearOnly = false)
    {
        _notes = (notes ?? Enumerable.Empty<TextNotePlacementInfo>())
            .Where(n => n != null && !string.IsNullOrWhiteSpace(n.Text))
            .ToList();
        _lines = (lines ?? Enumerable.Empty<DetailLinePlacementInfo>())
            .Where(l => l?.FromMm != null && l.ToMm != null)
            .ToList();
        _clearPrevious = clearPrevious;
        _clearOnly = clearOnly;
        _commentTag = string.IsNullOrWhiteSpace(commentTag) ? DefaultCommentTag : commentTag.Trim();
        _defaultTextTypeName = string.IsNullOrWhiteSpace(defaultTextTypeName)
            ? DefaultTextTypeName
            : defaultTextTypeName.Trim();
        _viewId = viewId;
        _placement = string.IsNullOrWhiteSpace(placement)
            ? DefaultPlacement
            : placement.Trim().ToLowerInvariant();
        _marginMm = marginMm > 0 ? marginMm : DefaultOutsideMarginMm;
        _paperWidthMm = paperWidthMm > 0 ? paperWidthMm : DefaultPaperWidthMm;
        _textSizeMm = textSizeMm > 0 ? textSizeMm : DefaultTextSizeMm;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public string GetName() => "Create Text Notes";

    public void Execute(UIApplication uiapp)
    {
        _uiApp = uiapp;
        try
        {
            var view = ResolveView();
            if (view is not ViewPlan && view is not ViewDrafting)
            {
                ResultInfo = new
                {
                    success = false,
                    message =
                        $"Active/target view must be a floor plan (or drafting). Got: {view?.ViewType} «{view?.Name}»."
                };
                return;
            }

            if (_notes.Count == 0 && _lines.Count == 0 && !_clearPrevious && !_clearOnly)
            {
                ResultInfo = new
                {
                    success = false,
                    message = "Pass notes[] and/or lines[], or clearPrevious/clearOnly=true to remove prior MCP annotations."
                };
                return;
            }

            // clearOnly: remove MCP annotations without creating notes or touching TextNoteType.
            if (_clearOnly)
            {
                using (var tx = new Transaction(Doc, "MCP Clear Text Notes"))
                {
                    tx.Start();
                    var clearedIds = ClearPreviousAnnotations(view, aggressive: true);
                    tx.Commit();
                    ResultInfo = new
                    {
                        success = true,
                        clearOnly = true,
                        view = view.Name,
                        viewId = view.Id.GetValue(),
                        commentTag = _commentTag,
                        createdNoteCount = 0,
                        createdLineCount = 0,
                        deletedPreviousCount = clearedIds.Count,
                        deletedPreviousIds = clearedIds
                    };
                }
                return;
            }

            var createdNotes = new List<object>();
            var createdLines = new List<object>();
            var notesWithDetailLeader = new HashSet<long>();
            var deletedIds = new List<long>();
            var errors = new List<string>();
            var usedTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var resolved = _notes
                .Select(n => ResolveNoteTargets(view, n))
                .ToList();
            var planBounds = GetPlanBoundsMm(view, resolved);
            var scale = Math.Max(view.Scale, 1);
            var boxWidthModelMm = _paperWidthMm * scale;
            var outsideOrigins = BuildOutsideOriginsBySide(
                planBounds, resolved, boxWidthModelMm);

            var sharedTypeId = ResolveTextNoteTypeId(null);
            var placedNotes = new List<(TextNote Note, int ResolvedIndex, string Side, XYZ LeaderEnd)>();

            using (var tx = new Transaction(Doc, "MCP Create Text Notes"))
            {
                tx.Start();

                if (_clearPrevious)
                    deletedIds = ClearPreviousAnnotations(view, aggressive: false);

                // Paper height 2.5 mm on ADSK_Замечания (keeps GOST Common / red from type).
                EnsureTextSizeMm(sharedTypeId, _textSizeMm);

                var autoIndex = 0;
                for (var i = 0; i < resolved.Count; i++)
                {
                    var noteInfo = resolved[i].Info;
                    try
                    {
                        XYZ forcedOrigin = null;
                        var side = "near";
                        if (noteInfo.LocationMm == null
                            && string.Equals(_placement, "outside", StringComparison.OrdinalIgnoreCase)
                            && outsideOrigins.TryGetValue(i, out var originInfo))
                        {
                            forcedOrigin = originInfo.Origin;
                            side = originInfo.Side;
                        }

                        var placed = PlaceTextNote(
                            view,
                            noteInfo,
                            resolved[i].LeaderEnd,
                            resolved[i].ElementPoint,
                            autoIndex,
                            forcedOrigin,
                            scale,
                            side,
                            out var typeName,
                            out var textNote);
                        if (placed == null || textNote == null)
                        {
                            errors.Add($"Could not resolve location for note: {Truncate(noteInfo.Text, 40)}");
                            continue;
                        }

                        createdNotes.Add(placed);
                        placedNotes.Add((textNote, i, side, resolved[i].LeaderEnd));
                        if (!string.IsNullOrEmpty(typeName))
                            usedTypeNames.Add(typeName);
                        autoIndex += 1;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{Truncate(noteInfo.Text, 40)}: {ex.Message}");
                    }
                }

                // Restack after regenerate so boxes don't overlap (use real heights).
                if (string.Equals(_placement, "outside", StringComparison.OrdinalIgnoreCase)
                    && placedNotes.Count > 0)
                {
                    Doc.Regenerate();
                    RestackOutsideNotes(view, placedNotes, planBounds, scale);
                    // After Coord move: draw detail leaders from final text position (both sides).
                    // Native TextNote.AddLeader is unreliable on the left column — skip it for outside.
                    Doc.Regenerate();
                    foreach (var p in placedNotes)
                    {
                        if (p.LeaderEnd == null)
                            continue;
                        var end = new XYZ(p.LeaderEnd.X, p.LeaderEnd.Y, view.Origin.Z);
                        // Start from plan-facing edge of the text box (not just Coord).
                        var from = LeaderStartFromNote(p.Note, p.Side, view);
                        var lineId = DrawDetailLeader(view, from, end);
                        if (lineId == null)
                        {
                            errors.Add($"Leader missing for note id {p.Note.Id.GetValue()} ({p.Side})");
                            continue;
                        }

                        createdLines.Add(new
                        {
                            lineId = lineId.Value,
                            textNoteId = p.Note.Id.GetValue(),
                            side = p.Side,
                            fromMm = new
                            {
                                x = Math.Round(from.X * 304.8, 1),
                                y = Math.Round(from.Y * 304.8, 1)
                            },
                            toMm = new
                            {
                                x = Math.Round(end.X * 304.8, 1),
                                y = Math.Round(end.Y * 304.8, 1)
                            }
                        });
                        notesWithDetailLeader.Add(p.Note.Id.GetValue());
                    }
                }

                foreach (var lineInfo in _lines)
                {
                    try
                    {
                        var from = DimensionAnnotationHelper.ConvertMmToFeet(lineInfo.FromMm);
                        var to = DimensionAnnotationHelper.ConvertMmToFeet(lineInfo.ToMm);
                        if (from.DistanceTo(to) < 1e-9)
                        {
                            errors.Add("Detail line from/to coincide — skipped");
                            continue;
                        }

                        var z = view.Origin.Z;
                        from = new XYZ(from.X, from.Y, z);
                        to = new XYZ(to.X, to.Y, z);

                        var curve = Line.CreateBound(from, to);
                        var detail = Doc.Create.NewDetailCurve(view, curve);
                        TagComments(detail, _commentTag);

                        createdLines.Add(new
                        {
                            lineId = detail.Id.GetValue(),
                            fromMm = new { x = lineInfo.FromMm.X, y = lineInfo.FromMm.Y },
                            toMm = new { x = lineInfo.ToMm.X, y = lineInfo.ToMm.Y }
                        });
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Detail line: {ex.Message}");
                    }
                }

                tx.Commit();
            }

            // Refresh locations after restack for the result payload
            if (placedNotes.Count > 0)
            {
                createdNotes = placedNotes.Select(p =>
                {
                    var c = p.Note.Coord;
                    var noteId = p.Note.Id.GetValue();
                    // Outside mode uses detail curves, not TextNote.LeaderCount.
                    var hasLeader = p.Note.LeaderCount > 0 || notesWithDetailLeader.Contains(noteId);
                    return (object)new
                    {
                        textNoteId = noteId,
                        elementId = resolved[p.ResolvedIndex].Info.ElementId,
                        text = p.Note.Text,
                        textType = usedTypeNames.FirstOrDefault() ?? "",
                        hasLeader,
                        placement = _placement,
                        side = p.Side,
                        textSizeMm = _textSizeMm,
                        paperWidthMm = _paperWidthMm,
                        locationMm = new
                        {
                            x = Math.Round(c.X * 304.8, 1),
                            y = Math.Round(c.Y * 304.8, 1)
                        }
                    };
                }).ToList();
            }

            ResultInfo = new
            {
                success = createdNotes.Count > 0
                    || createdLines.Count > 0
                    || (_clearPrevious && _notes.Count == 0 && _lines.Count == 0),
                view = view.Name,
                viewId = view.Id.GetValue(),
                commentTag = _commentTag,
                textTypesUsed = usedTypeNames.ToList(),
                textSizeMm = _textSizeMm,
                paperWidthMm = _paperWidthMm,
                viewScale = scale,
                createdNoteCount = createdNotes.Count,
                createdLineCount = createdLines.Count,
                deletedPreviousCount = deletedIds.Count,
                createdNotes,
                createdLines,
                deletedPreviousIds = deletedIds,
                errors
            };
        }
        catch (Exception ex)
        {
            ResultInfo = new
            {
                success = false,
                message = ex.Message
            };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    private sealed class ResolvedNote
    {
        public TextNotePlacementInfo Info { get; set; }
        public XYZ ElementPoint { get; set; }
        public XYZ LeaderEnd { get; set; }
    }

    private ResolvedNote ResolveNoteTargets(View view, TextNotePlacementInfo info)
    {
        XYZ elementPoint = null;
        if (info.ElementId is > 0)
            elementPoint = TryGetElementPoint(info.ElementId.Value, view);

        XYZ leaderEnd = null;
        var wantLeader = info.Leader != false;
        if (wantLeader && info.LeaderToMm != null)
            leaderEnd = DimensionAnnotationHelper.ConvertMmToFeet(info.LeaderToMm);
        else if (wantLeader && elementPoint != null)
            leaderEnd = elementPoint;

        return new ResolvedNote
        {
            Info = info,
            ElementPoint = elementPoint,
            LeaderEnd = leaderEnd
        };
    }

    /// <summary>
    /// Plan outline in model mm: rooms + walls + grids + leader targets (not view crop —
    /// crop on some floors is huge and pushes callout text far from leaders).
    /// </summary>
    private (double MinX, double MaxX, double MinY, double MaxY) GetPlanBoundsMm(
        View view,
        List<ResolvedNote> resolved)
    {
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        var any = false;

        void Acc(XYZ p)
        {
            if (p == null) return;
            var x = p.X * 304.8;
            var y = p.Y * 304.8;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
            any = true;
        }

        void AccBox(BoundingBoxXYZ bb)
        {
            if (bb == null) return;
            Acc(bb.Min);
            Acc(bb.Max);
        }

        foreach (var r in resolved)
        {
            Acc(r.LeaderEnd);
            Acc(r.ElementPoint);
        }

        foreach (var room in new FilteredElementCollector(Doc, view.Id)
                     .OfCategory(BuiltInCategory.OST_Rooms)
                     .WhereElementIsNotElementType())
        {
            AccBox(room.get_BoundingBox(view) ?? room.get_BoundingBox(null));
        }

        foreach (var wall in new FilteredElementCollector(Doc, view.Id)
                     .OfCategory(BuiltInCategory.OST_Walls)
                     .WhereElementIsNotElementType())
        {
            AccBox(wall.get_BoundingBox(view) ?? wall.get_BoundingBox(null));
        }

        // Axes / grids — annotations must sit outside bubble extent
        foreach (var grid in new FilteredElementCollector(Doc, view.Id)
                     .OfClass(typeof(Grid))
                     .Cast<Grid>())
        {
            AccBox(grid.get_BoundingBox(view) ?? grid.get_BoundingBox(null));
        }

        if (!any)
            return (-10000, 10000, -10000, 10000);

        return (minX, maxX, minY, maxY);
    }

    private sealed class OutsideOrigin
    {
        public XYZ Origin { get; set; }
        public string Side { get; set; }
    }

    /// <summary>
    /// Left column: Right-aligned insert at (minX - margin) — text grows away from plan,
    /// leader starts at Coord (near plan). Right column: Left-aligned at (maxX + margin).
    /// </summary>
    private Dictionary<int, OutsideOrigin> BuildOutsideOriginsBySide(
        (double MinX, double MaxX, double MinY, double MaxY) bounds,
        List<ResolvedNote> resolved,
        double boxWidthModelMm)
    {
        var result = new Dictionary<int, OutsideOrigin>();
        if (resolved.Count == 0)
            return result;

        var midX = (bounds.MinX + bounds.MaxX) / 2.0;
        var left = new List<(int Index, double Y)>();
        var right = new List<(int Index, double Y)>();

        for (var i = 0; i < resolved.Count; i++)
        {
            var p = resolved[i].LeaderEnd ?? resolved[i].ElementPoint;
            double xMm = p != null ? p.X * 304.8 : midX;
            double yMm = p != null ? p.Y * 304.8 : bounds.MaxY;

            var distLeft = Math.Abs(xMm - bounds.MinX);
            var distRight = Math.Abs(bounds.MaxX - xMm);
            if (distLeft <= distRight)
                left.Add((i, yMm));
            else
                right.Add((i, yMm));
        }

        // Anchor columns near leader targets on each side (not only global bbox —
        // avoids huge offset when crop region is wider than the building).
        var leftX = bounds.MinX - _marginMm;
        if (left.Count > 0)
        {
            var extremeLeftMm = left.Min(t =>
            {
                var p = resolved[t.Index].LeaderEnd ?? resolved[t.Index].ElementPoint;
                return p != null ? p.X * 304.8 : bounds.MinX;
            });
            leftX = Math.Max(extremeLeftMm - _marginMm, bounds.MinX - _marginMm);
        }

        var rightX = bounds.MaxX + _marginMm;
        if (right.Count > 0)
        {
            var extremeRightMm = right.Max(t =>
            {
                var p = resolved[t.Index].LeaderEnd ?? resolved[t.Index].ElementPoint;
                return p != null ? p.X * 304.8 : bounds.MaxX;
            });
            rightX = Math.Min(extremeRightMm + _marginMm, bounds.MaxX + _marginMm);
        }

        PlaceSideColumn(result, left, leftX, "left", bounds);
        PlaceSideColumn(result, right, rightX, "right", bounds);
        return result;
    }

    private void PlaceSideColumn(
        Dictionary<int, OutsideOrigin> result,
        List<(int Index, double Y)> side,
        double textXMm,
        string sideLabel,
        (double MinX, double MaxX, double MinY, double MaxY) bounds)
    {
        if (side.Count == 0)
            return;

        var ordered = side.OrderByDescending(s => s.Y).ToList();
        // Provisional even spacing; RestackOutsideNotes fixes overlaps using real bbox.
        var step = Math.Max(1500, (bounds.MaxY - bounds.MinY) / Math.Max(ordered.Count, 1));
        var topY = Math.Min(Math.Max(ordered[0].Y, bounds.MinY), bounds.MaxY);

        for (var i = 0; i < ordered.Count; i++)
        {
            var y = topY - i * step;
            result[ordered[i].Index] = new OutsideOrigin
            {
                Origin = new XYZ(textXMm / 304.8, y / 304.8, 0),
                Side = sideLabel
            };
        }
    }

    /// <summary>
    /// After create+regenerate: stack notes on each side by real height so lines don't overlap.
    /// Does not touch leaders — caller re-attaches them after restack (Coord move breaks them).
    /// </summary>
    private void RestackOutsideNotes(
        View view,
        List<(TextNote Note, int ResolvedIndex, string Side, XYZ LeaderEnd)> placed,
        (double MinX, double MaxX, double MinY, double MaxY) bounds,
        int scale)
    {
        var gapFt = (DefaultStackGapPaperMm * scale) / 304.8;
        var z = view.Origin.Z;

        foreach (var sideGroup in placed.GroupBy(p => p.Side))
        {
            if (sideGroup.Key != "left" && sideGroup.Key != "right")
                continue;

            var ordered = sideGroup
                .OrderByDescending(p => p.Note.Coord.Y)
                .ToList();

            var cursorTop = bounds.MaxY / 304.8 + (800.0 / 304.8);

            foreach (var item in ordered)
            {
                var note = item.Note;
                var bbox = note.get_BoundingBox(view);
                var height = bbox != null
                    ? Math.Abs(bbox.Max.Y - bbox.Min.Y)
                    : (_textSizeMm * scale * 2.0) / 304.8;

                var coord = note.Coord;
                double newY;
                if (bbox != null)
                {
                    var topOffset = bbox.Max.Y - coord.Y;
                    newY = cursorTop - topOffset;
                }
                else
                {
                    newY = cursorTop - height;
                }

                note.Coord = new XYZ(coord.X, newY, z);
                cursorTop -= height + gapFt;
            }
        }
    }

    private object PlaceTextNote(
        View view,
        TextNotePlacementInfo info,
        XYZ leaderEnd,
        XYZ elementPoint,
        int autoIndex,
        XYZ forcedOrigin,
        int scale,
        string side,
        out string typeName,
        out TextNote created)
    {
        typeName = string.Empty;
        created = null;

        XYZ textOrigin;
        if (info.LocationMm != null)
        {
            textOrigin = DimensionAnnotationHelper.ConvertMmToFeet(info.LocationMm);
        }
        else if (forcedOrigin != null)
        {
            textOrigin = forcedOrigin;
        }
        else if (elementPoint != null && string.Equals(_placement, "near", StringComparison.OrdinalIgnoreCase))
        {
            var offsetMm = info.OffsetMm > 0 ? info.OffsetMm : DefaultNearOffsetMm;
            var right = view.RightDirection.Normalize();
            var up = view.UpDirection.Normalize();
            var offsetFt = (offsetMm + autoIndex * 800) / 304.8;
            var sign = (autoIndex % 2 == 0) ? 1.0 : -1.0;
            textOrigin = elementPoint + right * (offsetFt * sign) + up * (offsetFt * 0.5);
        }
        else if (elementPoint != null)
        {
            textOrigin = elementPoint - view.RightDirection.Normalize() * (_marginMm / 304.8);
        }
        else
        {
            return null;
        }

        var z = view.Origin.Z;
        textOrigin = new XYZ(textOrigin.X, textOrigin.Y, z);
        if (leaderEnd != null)
            leaderEnd = new XYZ(leaderEnd.X, leaderEnd.Y, z);

        var typeId = ResolveTextNoteTypeId(info.TextTypeName);
        typeName = (Doc.GetElement(typeId) as TextNoteType)?.Name ?? string.Empty;

        var paperW = info.WidthMm > 0 ? info.WidthMm : _paperWidthMm;
        var widthFeet = ClampTextWidth(typeId, PaperMmToModelFeet(paperW, scale));

        // Left column: align Right so Coord is the plan-facing edge (leader starts here).
        // Right column: align Left so Coord faces the plan.
        var hAlign = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase)
            ? HorizontalTextAlignment.Right
            : HorizontalTextAlignment.Left;

        var options = new TextNoteOptions
        {
            TypeId = typeId,
            HorizontalAlignment = hAlign,
            VerticalAlignment = VerticalTextAlignment.Top
        };

        created = TextNote.Create(
            Doc,
            view.Id,
            textOrigin,
            widthFeet,
            NormalizeText(info.Text),
            options);

        TagMcpAnnotation(created, _commentTag);

        var hasLeader = false;
        if (leaderEnd != null && leaderEnd.DistanceTo(textOrigin) > 1e-6)
        {
            if (string.Equals(_placement, "outside", StringComparison.OrdinalIgnoreCase))
            {
                // Detail leaders drawn after restack (final Coord). Do not use native AddLeader —
                // it often fails / stays invisible on the left column.
                hasLeader = true;
            }
            else
            {
                hasLeader = DrawDetailLeader(view, created.Coord, leaderEnd) != null;
                AttachTextNoteLeader(created, leaderEnd);
            }
        }

        return new
        {
            textNoteId = created.Id.GetValue(),
            elementId = info.ElementId,
            text = created.Text,
            textType = typeName,
            hasLeader,
            placement = _placement,
            side,
            textSizeMm = _textSizeMm,
            paperWidthMm = paperW,
            locationMm = new
            {
                x = Math.Round(textOrigin.X * 304.8, 1),
                y = Math.Round(textOrigin.Y * 304.8, 1)
            },
            leaderToMm = leaderEnd == null
                ? null
                : new
                {
                    x = Math.Round(leaderEnd.X * 304.8, 1),
                    y = Math.Round(leaderEnd.Y * 304.8, 1)
                }
        };
    }

    /// <summary>
    /// Visible leader as a detail curve (works for left and right columns).
    /// Tagged MCP-ANN so clearPrevious removes it.
    /// Returns the created CurveElement id, or null on failure.
    /// </summary>
    private long? DrawDetailLeader(View view, XYZ from, XYZ to)
    {
        if (from == null || to == null || from.DistanceTo(to) < 1e-6)
            return null;

        try
        {
            var z = view.Origin.Z;
            var a = new XYZ(from.X, from.Y, z);
            var b = new XYZ(to.X, to.Y, z);
            var curve = Doc.Create.NewDetailCurve(view, Line.CreateBound(a, b));
            TagComments(curve, _commentTag);
            return curve.Id.GetValue();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Plan-facing edge of the note bbox (right edge for left column, left edge for right column).
    /// Falls back to Coord if bbox is unavailable.
    /// </summary>
    private static XYZ LeaderStartFromNote(TextNote note, string side, View view)
    {
        var z = view.Origin.Z;
        var bbox = note.get_BoundingBox(view);
        if (bbox == null)
            return new XYZ(note.Coord.X, note.Coord.Y, z);

        var midY = (bbox.Min.Y + bbox.Max.Y) / 2.0;
        var x = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase)
            ? bbox.Max.X
            : bbox.Min.X;
        return new XYZ(x, midY, z);
    }

    /// <summary>
    /// Native TextNote leader (optional). Only sets End — do not force Elbow.X = End.X
    /// (that collapsed left-column leaders).
    /// </summary>
    private static bool AttachTextNoteLeader(TextNote note, XYZ leaderEnd)
    {
        if (note == null || leaderEnd == null)
            return false;

        try
        {
            note.RemoveLeaders();
        }
        catch
        {
            // ignore
        }

        var origin = note.Coord;
        var targetIsToTheRight = leaderEnd.X >= origin.X;
        var leaderType = targetIsToTheRight
            ? TextNoteLeaderTypes.TNLT_STRAIGHT_R
            : TextNoteLeaderTypes.TNLT_STRAIGHT_L;

        try
        {
            var leader = note.AddLeader(leaderType);
            if (leader == null)
                return false;
            leader.End = leaderEnd;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureTextSizeMm(ElementId typeId, double sizeMm)
    {
        if (sizeMm <= 0 || typeId == ElementId.InvalidElementId)
            return;
        if (Doc.GetElement(typeId) is not TextNoteType type)
            return;

        var p = type.get_Parameter(BuiltInParameter.TEXT_SIZE);
        if (p == null || p.IsReadOnly)
            return;

        var targetFeet = sizeMm / 304.8;
        if (Math.Abs(p.AsDouble() - targetFeet) < 1e-9)
            return;

        p.Set(targetFeet);
    }

    private static double PaperMmToModelFeet(double paperMm, int scale)
    {
        // At scale 1:100, 70 mm on paper = 7000 mm in model.
        return (paperMm * Math.Max(scale, 1)) / 304.8;
    }

    private XYZ TryGetElementPoint(long elementId, View view)
    {
        var element = Doc.GetElement(Utils.ElementIdExtensions.FromLong(elementId));
        if (element == null)
            return null;

        if (element.Location is LocationPoint locPoint)
            return locPoint.Point;

        if (element.Location is LocationCurve locCurve)
        {
            var curve = locCurve.Curve;
            if (curve != null)
                return curve.Evaluate(0.5, true);
        }

        var bbox = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
        if (bbox != null)
            return (bbox.Min + bbox.Max) / 2.0;

        return null;
    }

    private ElementId ResolveTextNoteTypeId(string perNoteTypeName)
    {
        var preferred = !string.IsNullOrWhiteSpace(perNoteTypeName)
            ? perNoteTypeName.Trim()
            : _defaultTextTypeName;

        var types = new FilteredElementCollector(Doc)
            .OfClass(typeof(TextNoteType))
            .Cast<TextNoteType>()
            .ToList();

        if (!string.IsNullOrEmpty(preferred))
        {
            var exact = types.FirstOrDefault(t =>
                t.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact.Id;

            var partial = types.FirstOrDefault(t =>
                t.Name.IndexOf(preferred, StringComparison.OrdinalIgnoreCase) >= 0);
            if (partial != null)
                return partial.Id;

            // Soft preference for «Замечания» / Remarks when preferred missing
            if (preferred.IndexOf("Замеч", StringComparison.OrdinalIgnoreCase) >= 0
                || preferred.IndexOf("Remark", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var remarks = types.FirstOrDefault(t =>
                    t.Name.IndexOf("Замеч", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.Name.IndexOf("Remark", StringComparison.OrdinalIgnoreCase) >= 0);
                if (remarks != null)
                    return remarks.Id;
            }
        }

        var defaultId = Doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        if (defaultId != ElementId.InvalidElementId && Doc.GetElement(defaultId) is TextNoteType)
            return defaultId;

        return types.FirstOrDefault()?.Id
               ?? throw new InvalidOperationException(
                   "No TextNoteType in the project. Use a template with text styles (e.g. ADSK_Замечания).");
    }

    private double ClampTextWidth(ElementId typeId, double requestedFeet)
    {
        var minW = TextNote.GetMinimumAllowedWidth(Doc, typeId);
        var maxW = TextNote.GetMaximumAllowedWidth(Doc, typeId);
        return Math.Min(Math.Max(requestedFeet, minW), maxW);
    }

    private sealed class NoteClearContext
    {
        public BoundingBoxXYZ Bbox;
        public IList<XYZ> AnchorPoints = new List<XYZ>();
    }

    private List<long> ClearPreviousAnnotations(View view, bool aggressive)
    {
        var deleted = new List<long>();
        var toDelete = new HashSet<ElementId>();
        var noteContexts = new List<NoteClearContext>();

        foreach (var note in new FilteredElementCollector(Doc, view.Id)
                     .OfClass(typeof(TextNote))
                     .Cast<TextNote>())
        {
            if (!IsMcpAnnotation(note, aggressive))
                continue;

            toDelete.Add(note.Id);
            noteContexts.Add(BuildNoteClearContext(note, view));
        }

        // Detail lines / curves tagged by us, or leaders tied to norm callouts (aggressive)
        foreach (var curve in new FilteredElementCollector(Doc, view.Id)
                     .OfClass(typeof(CurveElement))
                     .Cast<CurveElement>())
        {
            if (IsTaggedMcp(curve))
            {
                toDelete.Add(curve.Id);
                continue;
            }

            if (!aggressive || noteContexts.Count == 0)
                continue;

            if (IsDetailLeaderForNotes(curve, noteContexts, view))
                toDelete.Add(curve.Id);
        }

        if (aggressive && TryGetPlanBoundsFeet(view, out var planMinX, out var planMaxX, out var planMinY, out var planMaxY))
        {
            var allCurves = new FilteredElementCollector(Doc, view.Id)
                .OfClass(typeof(CurveElement))
                .Cast<CurveElement>()
                .ToList();

            foreach (var curve in allCurves)
            {
                if (toDelete.Contains(curve.Id))
                    continue;
                if (IsTaggedMcp(curve))
                {
                    toDelete.Add(curve.Id);
                    continue;
                }

                if (!(curve.GeometryCurve is Line line))
                    continue;
                if (IsLikelyLeftoverMcpLeaderLine(line, planMinX, planMaxX, planMinY, planMaxY))
                    toDelete.Add(curve.Id);
            }

            ExpandConnectedLeaderSegments(allCurves, toDelete);
        }

        if (toDelete.Count == 0)
            return deleted;

        foreach (var id in toDelete)
            deleted.Add(id.GetValue());

        Doc.Delete(toDelete.ToList());
        return deleted;
    }

    private NoteClearContext BuildNoteClearContext(TextNote note, View view)
    {
        var ctx = new NoteClearContext();
        var z = view.Origin.Z;
        ctx.AnchorPoints.Add(new XYZ(note.Coord.X, note.Coord.Y, z));
        ctx.Bbox = note.get_BoundingBox(view);
        if (ctx.Bbox != null)
        {
            ctx.AnchorPoints.Add(new XYZ(ctx.Bbox.Min.X, ctx.Bbox.Min.Y, z));
            ctx.AnchorPoints.Add(new XYZ(ctx.Bbox.Max.X, ctx.Bbox.Max.Y, z));
            ctx.AnchorPoints.Add(new XYZ(ctx.Bbox.Min.X, ctx.Bbox.Max.Y, z));
            ctx.AnchorPoints.Add(new XYZ(ctx.Bbox.Max.X, ctx.Bbox.Min.Y, z));
            ctx.AnchorPoints.Add(new XYZ(
                (ctx.Bbox.Min.X + ctx.Bbox.Max.X) / 2.0,
                (ctx.Bbox.Min.Y + ctx.Bbox.Max.Y) / 2.0,
                z));
        }

        try
        {
            foreach (var leader in note.GetLeaders())
            {
                if (leader?.End != null)
                    ctx.AnchorPoints.Add(new XYZ(leader.End.X, leader.End.Y, z));
                if (leader?.Elbow != null)
                    ctx.AnchorPoints.Add(new XYZ(leader.Elbow.X, leader.Elbow.Y, z));
            }
        }
        catch
        {
            // ignore — detail leaders are matched by bbox/points
        }

        return ctx;
    }

    private static bool IsDetailLeaderForNotes(CurveElement curve, IList<NoteClearContext> contexts, View view)
    {
        if (curve?.GeometryCurve is not Line line)
            return false;

        var lengthMm = line.Length * 304.8;
        if (lengthMm > 20000)
            return false;

        var z = view.Origin.Z;
        var a = new XYZ(line.GetEndPoint(0).X, line.GetEndPoint(0).Y, z);
        var b = new XYZ(line.GetEndPoint(1).X, line.GetEndPoint(1).Y, z);
        const double pointTolFeet = 1500.0 / 304.8;
        const double bboxPadFeet = 800.0 / 304.8;

        foreach (var ctx in contexts)
        {
            foreach (var anchor in ctx.AnchorPoints)
            {
                var p = new XYZ(anchor.X, anchor.Y, z);
                if (p.DistanceTo(a) <= pointTolFeet || p.DistanceTo(b) <= pointTolFeet)
                    return true;
            }

            if (ctx.Bbox != null
                && (PointInExpandedBox(a, ctx.Bbox, bboxPadFeet, z)
                    || PointInExpandedBox(b, ctx.Bbox, bboxPadFeet, z)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PointInExpandedBox(XYZ point, BoundingBoxXYZ bbox, double padFeet, double z)
    {
        return point.X >= bbox.Min.X - padFeet
               && point.X <= bbox.Max.X + padFeet
               && point.Y >= bbox.Min.Y - padFeet
               && point.Y <= bbox.Max.Y + padFeet;
    }

    private bool TryGetPlanBoundsFeet(
        View view,
        out double minX,
        out double maxX,
        out double minY,
        out double maxY)
    {
        minX = maxX = minY = maxY = 0;
        var mnX = double.PositiveInfinity;
        var mxX = double.NegativeInfinity;
        var mnY = double.PositiveInfinity;
        var mxY = double.NegativeInfinity;
        var any = false;

        void Acc(BoundingBoxXYZ bb)
        {
            if (bb == null)
                return;
            mnX = Math.Min(mnX, bb.Min.X);
            mxX = Math.Max(mxX, bb.Max.X);
            mnY = Math.Min(mnY, bb.Min.Y);
            mxY = Math.Max(mxY, bb.Max.Y);
            any = true;
        }

        foreach (var room in new FilteredElementCollector(Doc, view.Id)
                     .OfCategory(BuiltInCategory.OST_Rooms)
                     .WhereElementIsNotElementType())
        {
            Acc(room.get_BoundingBox(view));
        }

        foreach (var wall in new FilteredElementCollector(Doc, view.Id)
                     .OfCategory(BuiltInCategory.OST_Walls)
                     .WhereElementIsNotElementType())
        {
            Acc(wall.get_BoundingBox(view));
        }

        if (!any)
            return false;

        minX = mnX;
        maxX = mxX;
        minY = mnY;
        maxY = mxY;
        return true;
    }

    private static bool IsLikelyLeftoverMcpLeaderLine(
        Line line,
        double planMinX,
        double planMaxX,
        double planMinY,
        double planMaxY)
    {
        var lengthMm = line.Length * 304.8;
        if (lengthMm > 25000)
            return false;

        var a = line.GetEndPoint(0);
        var b = line.GetEndPoint(1);

        if (IsOrphanOutsideLeader(line, planMinX, planMaxX, planMinY, planMaxY))
            return true;

        if (lengthMm > 15000)
            return false;

        const double edgeFeet = 500.0 / 304.8;
        const double columnFeet = (DefaultOutsideMarginMm + 2000.0) / 304.8;

        bool InLeftColumn(double x) => x < planMinX - edgeFeet;
        bool InRightColumn(double x) => x > planMaxX + edgeFeet;
        bool InTopBand(double y) => y > planMaxY + edgeFeet;
        bool InBottomBand(double y) => y < planMinY - edgeFeet;

        if (InLeftColumn(a.X) || InLeftColumn(b.X)
            || InRightColumn(a.X) || InRightColumn(b.X)
            || InTopBand(a.Y) || InTopBand(b.Y)
            || InBottomBand(a.Y) || InBottomBand(b.Y))
        {
            return true;
        }

        // Short stub fully in the outside margin (both ends left/right of plan)
        if (a.X < planMinX - columnFeet && b.X < planMinX - columnFeet)
            return true;
        if (a.X > planMaxX + columnFeet && b.X > planMaxX + columnFeet)
            return true;

        return false;
    }

    private static void ExpandConnectedLeaderSegments(
        IList<CurveElement> allCurves,
        HashSet<ElementId> toDelete)
    {
        const double tolFeet = 600.0 / 304.8;
        var anchorPoints = new List<XYZ>();

        foreach (var curve in allCurves)
        {
            if (!toDelete.Contains(curve.Id))
                continue;
            if (!(curve.GeometryCurve is Line line))
                continue;
            anchorPoints.Add(line.GetEndPoint(0));
            anchorPoints.Add(line.GetEndPoint(1));
        }

        if (anchorPoints.Count == 0)
            return;

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var curve in allCurves)
            {
                if (toDelete.Contains(curve.Id))
                    continue;
                if (!(curve.GeometryCurve is Line line))
                    continue;
                if (line.Length * 304.8 > 15000)
                    continue;

                var a = line.GetEndPoint(0);
                var b = line.GetEndPoint(1);
                foreach (var p in anchorPoints)
                {
                    if (p.DistanceTo(a) <= tolFeet || p.DistanceTo(b) <= tolFeet)
                    {
                        toDelete.Add(curve.Id);
                        anchorPoints.Add(a);
                        anchorPoints.Add(b);
                        changed = true;
                        break;
                    }
                }
            }
        }
    }

    private static bool IsOrphanOutsideLeader(
        Line line,
        double planMinX,
        double planMaxX,
        double planMinY,
        double planMaxY)
    {
        const double marginFeet = 2500.0 / 304.8;
        var a = line.GetEndPoint(0);
        var b = line.GetEndPoint(1);

        bool Outside(double x, double y) =>
            x < planMinX - marginFeet
            || x > planMaxX + marginFeet
            || y < planMinY - marginFeet
            || y > planMaxY + marginFeet;

        return Outside(a.X, a.Y) != Outside(b.X, b.Y);
    }

    private bool CommentsStartWithTag(Element element)
    {
        return IsTaggedMcp(element);
    }

    private bool IsTaggedMcp(Element element)
    {
        var c = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()
                ?? string.Empty;
        if (c.StartsWith(_commentTag, StringComparison.OrdinalIgnoreCase))
            return true;

        var mark = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString()
                   ?? string.Empty;
        return mark.StartsWith(_commentTag, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsMcpAnnotation(TextNote note, bool aggressive)
    {
        if (note == null)
            return false;
        if (CommentsStartWithTag(note))
            return true;
        var text = note.Text ?? string.Empty;
        if (text.EndsWith(TextMarkerSuffix, StringComparison.Ordinal))
            return true;
        if (!aggressive)
            return false;
        return LooksLikeNormViolationNote(text);
    }

    private static bool LooksLikeNormViolationNote(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = text.ToLowerInvariant();

        if (t.Contains("превыш") || t.Contains("наруш"))
            return true;
        if (t.Contains("глубин") && (t.Contains("свету") || t.Contains("комнат")))
            return true;
        if (t.Contains(" · ") && (t.Contains("п.") || t.Contains("сп рк") || t.Contains("гост")))
            return true;
        if ((t.Contains("п. ") || t.Contains("п.")) &&
            (t.Contains("сп рк") || t.Contains("гост") || t.Contains("4.")))
            return true;

        if (!t.Contains("мм"))
            return false;
        return t.Contains("превыш")
               || t.Contains("наруш")
               || t.Contains(" · ")
               || t.Contains("п. ")
               || t.Contains("сп рк")
               || t.Contains("гост");
    }

    private static void TagComments(Element element, string tag)
    {
        var comments = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (comments != null && !comments.IsReadOnly)
        {
            comments.Set(tag);
            return;
        }

        var mark = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
        if (mark != null && !mark.IsReadOnly)
            mark.Set(tag);
    }

    private void TagMcpAnnotation(TextNote note, string tag)
    {
        if (note == null)
            return;
        TagComments(note, tag);
        if (CommentsStartWithTag(note))
            return;
        try
        {
            var text = note.Text ?? string.Empty;
            if (!text.EndsWith(TextMarkerSuffix, StringComparison.Ordinal))
                note.Text = text + TextMarkerSuffix;
        }
        catch
        {
            // ignore — clear may still find by norm text when clearOnly aggressive
        }
    }

    private View ResolveView()
    {
        if (_viewId > 0)
        {
            var el = Doc.GetElement(Utils.ElementIdExtensions.FromLong(_viewId));
            if (el is View v)
                return v;
        }

        return _uiApp.ActiveUIDocument.ActiveView;
    }

    private static string NormalizeText(string text)
    {
        return (text ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Trim();
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text ?? string.Empty;
        return text.Substring(0, max) + "…";
    }
}
