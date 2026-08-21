using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// The «ключевые параметры» a model snapshot records (REV-170) — a fixed, small
    /// list rather than everything an element carries.
    /// </summary>
    /// <remarks>
    /// The list is short on purpose. A snapshot of a 300 000-element model has to take
    /// a minute, not half an hour, and walking <c>Element.Parameters</c> means tens of
    /// millions of value reads: that alone is the difference. What is kept is what an
    /// architect would call a change — marking, phase, level and offsets, and the
    /// dimensions Revit computes for walls, floors and rooms.
    ///
    /// Keys are <c>BuiltInParameter</c> names, never the display names: those are
    /// localised, and Revit on the test machine switches language between sessions.
    /// A snapshot keyed on «Марка» and one keyed on «Mark» would share not a single
    /// parameter, and the diff would read as though every element in the model had
    /// been rewritten. The display names travel alongside, once per page, so the diff
    /// can still speak Russian.
    ///
    /// Anything outside the list — ADSK_ parameters, an office's own shared
    /// parameters — is asked for by name through <c>extraParameters</c>.
    /// </remarks>
    public static class SnapshotParameterSet
    {
        /// <summary>
        /// Read for every element that has them. A parameter missing on a category is
        /// simply absent from that element's map, which is what keeps the map small.
        /// </summary>
        public static readonly BuiltInParameter[] KeyParameters =
        {
            // Identity an architect assigns by hand.
            BuiltInParameter.ALL_MODEL_MARK,
            BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,

            // Phase. A demolished element is a change, and without this it looks
            // untouched: it is still there, with the same geometry.
            BuiltInParameter.PHASE_CREATED,
            BuiltInParameter.PHASE_DEMOLISHED,

            // Where it is pinned vertically. Position is in the bounding box, but the
            // constraint says whether a move was deliberate or a level being edited.
            BuiltInParameter.FAMILY_LEVEL_PARAM,
            BuiltInParameter.SCHEDULE_LEVEL_PARAM,
            BuiltInParameter.INSTANCE_ELEVATION_PARAM,
            BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM,
            BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM,
            BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM,
            BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
            BuiltInParameter.FAMILY_TOP_LEVEL_PARAM,
            BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM,
            BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM,

            // Walls: the constraints that decide the height an architect actually edits.
            BuiltInParameter.WALL_BASE_CONSTRAINT,
            BuiltInParameter.WALL_HEIGHT_TYPE,
            BuiltInParameter.WALL_BASE_OFFSET,
            BuiltInParameter.WALL_TOP_OFFSET,
            BuiltInParameter.WALL_USER_HEIGHT_PARAM,
            BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT,

            // Floors.
            BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM,

            // Levels themselves — a whole floor moving is the loudest change there is.
            BuiltInParameter.LEVEL_ELEV,

            // Computed sizes. Read-only, and REV-171 will filter recomputed noise out
            // of the report — but «площадь квартиры выросла на 4 м²» is the sentence
            // the whole эпик exists to produce, so the numbers have to be recorded.
            BuiltInParameter.CURVE_ELEM_LENGTH,
            BuiltInParameter.HOST_AREA_COMPUTED,
            BuiltInParameter.HOST_VOLUME_COMPUTED,

            // Rooms.
            BuiltInParameter.ROOM_NAME,
            BuiltInParameter.ROOM_NUMBER,
            BuiltInParameter.ROOM_AREA,
            BuiltInParameter.ROOM_PERIMETER,
            BuiltInParameter.ROOM_HEIGHT,
            BuiltInParameter.ROOM_UPPER_OFFSET,
        };

        /// <summary>Stable key for a built-in parameter — the enum name, not the label.</summary>
        public static string KeyOf(BuiltInParameter parameter) => parameter.ToString();

        /// <summary>
        /// Read the key parameters of one element into <paramref name="into"/>, and note
        /// each key's display name in <paramref name="labels"/> the first time it is seen.
        /// </summary>
        public static void Collect(
            Document doc,
            Element element,
            IList<string> extraParameterNames,
            Dictionary<string, object> into,
            Dictionary<string, string> labels)
        {
            foreach (var bip in KeyParameters)
            {
                Parameter parameter;
                try
                {
                    parameter = element.get_Parameter(bip);
                }
                catch
                {
                    // A category that does not know the parameter is the normal case
                    // and answers null, but a few of them throw instead.
                    continue;
                }

                if (parameter == null || !parameter.HasValue) continue;

                var value = ReadValue(doc, parameter);
                if (value == null) continue;

                var key = KeyOf(bip);
                into[key] = value;
                if (!labels.ContainsKey(key))
                {
                    var label = parameter.Definition?.Name;
                    if (!string.IsNullOrEmpty(label)) labels[key] = label;
                }
            }

            if (extraParameterNames == null) return;

            foreach (var name in extraParameterNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;

                Parameter parameter;
                try
                {
                    parameter = element.LookupParameter(name);
                }
                catch
                {
                    continue;
                }

                if (parameter == null || !parameter.HasValue) continue;

                var value = ReadValue(doc, parameter);
                if (value == null) continue;

                into[name] = value;
                if (!labels.ContainsKey(name)) labels[name] = name;
            }
        }

        /// <summary>
        /// The raw value, in Revit's own units. Deliberately not
        /// <c>AsValueString()</c>: that is rounded to the project's display precision
        /// and translated, so a hash over it would change when someone switches the
        /// units to centimetres, and again when Revit comes up in English.
        /// </summary>
        private static object ReadValue(Document doc, Parameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return parameter.AsString();

                case StorageType.Integer:
                    return (long)parameter.AsInteger();

                case StorageType.Double:
                    return parameter.AsDouble();

                case StorageType.ElementId:
                    var id = parameter.AsElementId();
                    if (id == null || id == ElementId.InvalidElementId) return null;
                    // The name, not the number: an id means nothing across two files,
                    // and «Уровень 3 → Уровень 4» is what the architect needs to read.
                    var target = doc.GetElement(id);
                    return target?.Name ?? id.GetValue().ToString();

                default:
                    return null;
            }
        }
    }
}
