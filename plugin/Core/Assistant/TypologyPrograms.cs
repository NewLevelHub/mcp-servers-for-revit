using System.Collections.Generic;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Room programs and layout logic for commercial typologies (café, office, STO, car wash).
    /// Used by scenario presets and system prompt — not geometry templates.
    /// </summary>
    public static class TypologyPrograms
    {
        public sealed class Typology
        {
            public string Id { get; set; }
            public string Label { get; set; }
            public string Icon { get; set; }
            /// <summary>Typical footprint hint for the architect (m²).</summary>
            public string FootprintHint { get; set; }
            /// <summary>Norm catalog topics for query_norm_rules.</summary>
            public IReadOnlyList<string> NormTopics { get; set; }
            /// <summary>Rooms with min areas and adjacency hints.</summary>
            public IReadOnlyList<RoomSlot> Rooms { get; set; }
            /// <summary>Layout logic the agent must follow.</summary>
            public string LayoutLogic { get; set; }
            /// <summary>Default user-facing prompt.</summary>
            public string DefaultPrompt { get; set; }
        }

        public sealed class RoomSlot
        {
            public string Name { get; set; }
            public string Purpose { get; set; }
            public double? MinAreaSqM { get; set; }
            public string Adjacency { get; set; }
        }

        public static IReadOnlyList<Typology> All { get; } = new[]
        {
            Cafe40,
            OfficeOpen,
            StoSmall,
            CarWashTunnel,
            SchoolWing,
            ResidentialFlat,
        };

        public static Typology GetById(string id)
        {
            foreach (var t in All)
            {
                if (t.Id == id)
                    return t;
            }
            return null;
        }

        /// <summary>Café / общепит ~40 мест.</summary>
        public static Typology Cafe40 { get; } = new Typology
        {
            Id = "cafe_40",
            Label = "Кафе",
            Icon = "☕",
            FootprintHint = "контур ~180–220 м² (зал ≥120 м² при 40 местах)",
            NormTopics = new[] { "кафе", "общепит", "тамбур", "мгн", "санузел" },
            Rooms = new[]
            {
                new RoomSlot { Name = "Зал", Purpose = "Посетители", MinAreaSqM = 120, Adjacency = "тамбур (или гостевой коридор), холл санузлов, бар; кухня — только дверь раздачи" },
                new RoomSlot { Name = "Тамбур", Purpose = "Входная группа", MinAreaSqM = 2.7, Adjacency = "улица (вход на фасаде), сразу зал или короткий гостевой коридор — НЕ кухня/мойка/склад" },
                new RoomSlot { Name = "Бар / касса", Purpose = "Посетители", MinAreaSqM = 6, Adjacency = "зал (открыто или дверь), не блокирует путь тамбур→зал" },
                new RoomSlot { Name = "Холл санузлов", Purpose = "Санитарная зона", MinAreaSqM = 4, Adjacency = "только зал или гостевой коридор; дверь М и Ж — только из этого холла" },
                new RoomSlot { Name = "Санузел М", Purpose = "Санитарная зона", MinAreaSqM = 2, Adjacency = "ТОЛЬКО холл санузлов — запрет: зал, кухня, мойка, служебный коридор" },
                new RoomSlot { Name = "Санузел Ж", Purpose = "Санитарная зона", MinAreaSqM = 2, Adjacency = "ТОЛЬКО холл санузлов — та же дверь/хост, что и у М (симметрия гостевого блока)" },
                new RoomSlot { Name = "Санузел МГН", Purpose = "Санитарная зона", MinAreaSqM = 4.8, Adjacency = "холл санузлов; путь из зала без лестниц и без служебных зон" },
                new RoomSlot { Name = "Кухня / пищеблок", Purpose = "Производство", MinAreaSqM = 25, Adjacency = "зал (раздача), мойка, кладовая; не на гостевом пути" },
                new RoomSlot { Name = "Мойка", Purpose = "Производство", MinAreaSqM = 8, Adjacency = "кухня / служебный коридор — не гостевой" },
                new RoomSlot { Name = "Кладовая", Purpose = "Производство", MinAreaSqM = 6, Adjacency = "кухня / служебный коридор" },
                new RoomSlot { Name = "Персонал / раздевалка", Purpose = "Персонал", MinAreaSqM = 8, Adjacency = "кухня или служебный коридор; санузел персонала — из персонала, не из зала" },
                new RoomSlot { Name = "Служебный коридор", Purpose = "Циркуляция персонала", MinAreaSqM = null, Adjacency = "кухня, мойка, кладовая, персонал — отделён от гостевого пути" }
            },
            LayoutLogic =
                "ЦИРКУЛЯЦИЯ ГОСТЕЙ: улица → тамбур на уличном фасаде → зал (прямо или через короткий гостевой коридор). " +
                "Тамбур НЕ ставить в угол «кухня/склад»; входная дверь — середина наружной стены фасада. " +
                "САНУЗЛЫ ГОСТЕЙ: блок «холл санузлов + М + Ж (+МГН)» у зала. " +
                "hostWallId двери М и Ж = перегородка холл↔кабина; ЗАПРЕЩЕНО вешать гостевой WC на стену зала, кухни, мойки или служебного коридора. " +
                "Нельзя: М через зал, Ж через техзону — оба WC одинаково из холла. " +
                "ПРОИЗВОДСТВО: кухня+мойка+кладовая+персонал сбоку/сзади; служебный коридор не пересекает гостевой путь. " +
                "Раздача кухня↔зал — одна дверь; locationPoint слегка сместить В КУХНЮ (или facingFlipped), чтобы полотно не било в зал. " +
                "МГН: дверь ≥900 мм, путь из зала без лестниц. Окна — на зал (фасад). " +
                "НЕ линейная «коробка в ряд» — зал крупный, сервис сбоку.",
            DefaultPrompt =
                "Спроектируй кафе на 40 мест на активном плане: зал, тамбур на фасаде, бар, холл санузлов + М/Ж/МГН, " +
                "кухня, мойка, кладовая, персонал, служебный коридор. Гостевой и служебный пути разделены. " +
                "Стены, двери (hostWallId + ориентация в комнату), окна, марки с площадью, цвет по назначению, размеры. Проверь нормы."
        };

        /// <summary>Open office ~15–25 workplaces.</summary>
        public static Typology OfficeOpen { get; } = new Typology
        {
            Id = "office_open",
            Label = "Офис",
            Icon = "🏢",
            FootprintHint = "контур ~120–200 м²",
            NormTopics = new[] { "офис", "административное", "мгн", "коридор эвакуации", "тамбур" },
            Rooms = new[]
            {
                new RoomSlot { Name = "Open space", Purpose = "Рабочая зона", MinAreaSqM = 60, Adjacency = "коридор, переговорная" },
                new RoomSlot { Name = "Переговорная", Purpose = "Рабочая зона", MinAreaSqM = 12, Adjacency = "open space" },
                new RoomSlot { Name = "Кабинет руководителя", Purpose = "Рабочая зона", MinAreaSqM = 15, Adjacency = "open space, коридор" },
                new RoomSlot { Name = "Ресепшен / холл", Purpose = "Входная группа", MinAreaSqM = 15, Adjacency = "вход, open space" },
                new RoomSlot { Name = "Тамбур", Purpose = "Входная группа", MinAreaSqM = 3, Adjacency = "улица, холл" },
                new RoomSlot { Name = "Кухня-столовая", Purpose = "Бытовая зона", MinAreaSqM = 12, Adjacency = "open space" },
                new RoomSlot { Name = "Санузел М/Ж", Purpose = "Санитарная зона", MinAreaSqM = 4, Adjacency = "коридор" },
                new RoomSlot { Name = "Серверная / ИТ", Purpose = "Техническая", MinAreaSqM = 6, Adjacency = "коридор" },
                new RoomSlot { Name = "Коридор", Purpose = "Циркуляция", MinAreaSqM = null, Adjacency = "связывает все зоны, ширина по норме эвакуации" }
            },
            LayoutLogic =
                "Вход → тамбур → ресепшен → open space по центру/у окна. " +
                "Переговорные и кабинет — по периметру. Коридор-кольцо или проход 1,2–1,5 м. " +
                "Санузлы у коридора, не через open space.",
            DefaultPrompt =
                "Спроектируй офис open space ~20 рабочих мест: ресепшен, тамбур, open space, " +
                "переговорная, кабинет, кухня, санузлы, серверная. Полный цикл: стены, двери, окна, марки, цвет зон."
        };

        /// <summary>Small auto service (СТО) 2–3 поста.</summary>
        public static Typology StoSmall { get; } = new Typology
        {
            Id = "sto_small",
            Label = "СТО",
            Icon = "🔧",
            FootprintHint = "контур ~250–400 м²",
            NormTopics = new[] { "сто", "автосервис", "производственное", "эвакуация", "мгн" },
            Rooms = new[]
            {
                new RoomSlot { Name = "Пост 1", Purpose = "Производство", MinAreaSqM = 40, Adjacency = "проезд, зона обслуживания" },
                new RoomSlot { Name = "Пост 2", Purpose = "Производство", MinAreaSqM = 40, Adjacency = "пост 1" },
                new RoomSlot { Name = "Зона обслуживания / приёмка", Purpose = "Посетители", MinAreaSqM = 25, Adjacency = "вход, посты" },
                new RoomSlot { Name = "Склад ЗИП", Purpose = "Склад", MinAreaSqM = 15, Adjacency = "посты" },
                new RoomSlot { Name = "Раздевалка / душ", Purpose = "Персонал", MinAreaSqM = 12, Adjacency = "служебный вход" },
                new RoomSlot { Name = "Санузел", Purpose = "Санитарная зона", MinAreaSqM = 4, Adjacency = "раздевалка" },
                new RoomSlot { Name = "Администрация", Purpose = "Администрация", MinAreaSqM = 12, Adjacency = "приёмка" }
            },
            LayoutLogic =
                "Въезд/ворота с одной стороны, посты в ряд или L-образно с проездом 6+ м. " +
                "Приёмка у входа клиентов. Склад и персонал — сзади. Высота ворот — по типу ТС (если в запросе).",
            DefaultPrompt =
                "Спроектируй СТО на 2 поста: зона приёмки, 2 поста обслуживания, склад, раздевалка, санузел, админ. " +
                "Стены, ворота/двери, помещения, марки. Учти проезд между постами."
        };

        /// <summary>Car wash — tunnel or self-service bays.</summary>
        public static Typology CarWashTunnel { get; } = new Typology
        {
            Id = "car_wash",
            Label = "Автомойка",
            Icon = "🚗",
            FootprintHint = "контур ~150–300 м² (2–4 поста)",
            NormTopics = new[] { "автомойка", "кір жуу", "бытовое обслуживание", "водоотведение", "эвакуация" },
            Rooms = new[]
            {
                new RoomSlot { Name = "Пост мойки 1", Purpose = "Производство", MinAreaSqM = 25, Adjacency = "въезд, выезд" },
                new RoomSlot { Name = "Пост мойки 2", Purpose = "Производство", MinAreaSqM = 25, Adjacency = "пост 1" },
                new RoomSlot { Name = "Техническая / насосная", Purpose = "Техническая", MinAreaSqM = 10, Adjacency = "посты" },
                new RoomSlot { Name = "Касса / зона ожидания", Purpose = "Посетители", MinAreaSqM = 15, Adjacency = "вход" },
                new RoomSlot { Name = "Санузел", Purpose = "Санитарная зона", MinAreaSqM = 4, Adjacency = "ожидание" },
                new RoomSlot { Name = "Персонал", Purpose = "Персонал", MinAreaSqM = 8, Adjacency = "техническая" }
            },
            LayoutLogic =
                "Линейная или Г-образная схема: въезд → посты → выезд. Ширина поста ~3,5–4 м, длина ~6–8 м. " +
                "Касса у входа клиентов. Техпомещение — сбоку от постов. Дренаж/канализация — заложить в логике, не рисовать трубами в v1.",
            DefaultPrompt =
                "Спроектируй автомойку на 2 поста: посты мойки, касса/ожидание, техпомещение, санузел, персонал. " +
                "Въезд и выезд, стены, двери, марки помещений."
        };

        /// <summary>Small school wing / floor fragment (not a full school building).</summary>
        public static Typology SchoolWing { get; } = new Typology
        {
            Id = "school_wing",
            Label = "Школа",
            Icon = "🏫",
            FootprintHint = "фрагмент этажа ~200–350 м² (не всё здание)",
            NormTopics = new[] { "школа", "учебное", "коридор эвакуации", "класс", "санузел" },
            Rooms = new[]
            {
                new RoomSlot { Name = "Вестибюль", Purpose = "Входная группа", MinAreaSqM = 20, Adjacency = "улица, коридор" },
                new RoomSlot { Name = "Коридор", Purpose = "Циркуляция", MinAreaSqM = null, Adjacency = "связывает классы, ширина ≥1,5–2,2 м по нормам" },
                new RoomSlot { Name = "Класс 1", Purpose = "Учебная", MinAreaSqM = 50, Adjacency = "коридор, окна на фасад" },
                new RoomSlot { Name = "Класс 2", Purpose = "Учебная", MinAreaSqM = 50, Adjacency = "коридор, окна на фасад" },
                new RoomSlot { Name = "Класс 3", Purpose = "Учебная", MinAreaSqM = 50, Adjacency = "коридор, окна на фасад" },
                new RoomSlot { Name = "Учительская", Purpose = "Персонал", MinAreaSqM = 16, Adjacency = "коридор" },
                new RoomSlot { Name = "Санузел М/Ж", Purpose = "Санитарная зона", MinAreaSqM = 8, Adjacency = "коридор у вестибюля" },
            },
            LayoutLogic =
                "Вход → вестибюль → коридор-ось. Классы с одной или двух сторон коридора, окна на фасад. " +
                "Учительская и санузлы у коридора, не вместо класса. " +
                "НЕ две комнаты с одной перегородкой — минимум вестибюль+коридор+3 класса+учительская+санузел.",
            DefaultPrompt =
                "Спроектируй фрагмент школьного этажа: вестибюль, коридор, 3 класса, учительская, санузлы. " +
                "Стены, двери, марки, цвет по назначению."
        };

        /// <summary>One residential apartment / flat layout.</summary>
        public static Typology ResidentialFlat { get; } = new Typology
        {
            Id = "residential_flat",
            Label = "Жилой дом",
            Icon = "🏠",
            FootprintHint = "квартира ~60–90 м²",
            NormTopics = new[] { "жилое", "квартира", "кухня", "санузел", "эвакуация" },
            Rooms = new[]
            {
                new RoomSlot { Name = "Прихожая", Purpose = "Входная группа", MinAreaSqM = 4, Adjacency = "вход, коридор/гостиная" },
                new RoomSlot { Name = "Гостиная", Purpose = "Жилая", MinAreaSqM = 16, Adjacency = "прихожая, кухня" },
                new RoomSlot { Name = "Кухня", Purpose = "Кухня", MinAreaSqM = 8, Adjacency = "гостиная" },
                new RoomSlot { Name = "Спальня 1", Purpose = "Жилая", MinAreaSqM = 12, Adjacency = "коридор" },
                new RoomSlot { Name = "Спальня 2", Purpose = "Жилая", MinAreaSqM = 10, Adjacency = "коридор" },
                new RoomSlot { Name = "Санузел", Purpose = "Санитарная зона", MinAreaSqM = 4, Adjacency = "коридор/прихожая" },
            },
            LayoutLogic =
                "Вход → прихожая → гостиная/кухня (открытая или смежные). Спальни и санузел от коридора. " +
                "Не две одинаковые «коробки» — зонирование жилой квартиры.",
            DefaultPrompt =
                "Спроектируй квартиру: прихожая, гостиная, кухня, 2 спальни, санузел. Стены, двери, марки."
        };

        /// <summary>Map ask_user / free-text answer to a known typology (REV-125 follow-up).</summary>
        public static Typology MatchFromAnswer(string answer)
        {
            var t = (answer ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(t))
                return null;

            if (Contains(t, "школ", "класс", "учебн"))
                return SchoolWing;
            if (Contains(t, "жилой", "квартир", "апартамент", "жил "))
                return ResidentialFlat;
            if (Contains(t, "офис", "open space", "open-space"))
                return OfficeOpen;
            if (Contains(t, "кафе", "ресторан", "общепит"))
                return Cafe40;
            if (Contains(t, "сто", "автосервис"))
                return StoSmall;
            if (Contains(t, "автомойк", "мойк", "car wash"))
                return CarWashTunnel;

            foreach (var typ in All)
            {
                if (!string.IsNullOrWhiteSpace(typ.Label)
                    && t.IndexOf(typ.Label.ToLowerInvariant(), System.StringComparison.Ordinal) >= 0)
                    return typ;
            }

            return null;
        }

        /// <summary>Guidance block injected into ask_user tool result.</summary>
        public static string BuildHintForAnswer(string answer)
        {
            var typ = MatchFromAnswer(answer);
            return typ == null ? "" : BuildAgentInstruction(typ);
        }

        private static bool Contains(string text, params string[] needles)
        {
            foreach (var n in needles)
            {
                if (!string.IsNullOrEmpty(n) && text.IndexOf(n, System.StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>Build AgentInstruction text from a typology definition.</summary>
        public static string BuildAgentInstruction(Typology t)
        {
            if (t == null)
                return "";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("ТИПОЛОГИЯ: " + t.Label + ". " + t.FootprintHint);
            sb.AppendLine("Нормы: query_norm_rules topics=" + string.Join(", ", t.NormTopics) + " — площади только из каталога.");
            sb.AppendLine("ПРОГРАММА ПОМЕЩЕНИЙ (обязательна — не своди к двум комнатам):");
            foreach (var r in t.Rooms)
            {
                var area = r.MinAreaSqM.HasValue ? $" ≥{r.MinAreaSqM:0.#} м²" : "";
                sb.AppendLine($"  • {r.Name} ({r.Purpose}){area} — {r.Adjacency}");
            }
            sb.AppendLine("ЛОГИКА: " + t.LayoutLogic);
            sb.AppendLine(AssistantPlaybooks.TypologyAgentRules);
            return sb.ToString().Trim();
        }
    }
}
