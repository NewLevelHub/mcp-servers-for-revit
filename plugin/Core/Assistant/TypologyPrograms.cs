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
            CarWashTunnel
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
                new RoomSlot { Name = "Зал", Purpose = "Посетители", MinAreaSqM = 120, Adjacency = "тамбур, холл санузлов, кухня" },
                new RoomSlot { Name = "Тамбур", Purpose = "Входная группа", MinAreaSqM = 2.7, Adjacency = "улица (вход), зал" },
                new RoomSlot { Name = "Холл санузлов", Purpose = "Санитарная зона", MinAreaSqM = 4, Adjacency = "зал" },
                new RoomSlot { Name = "Санузел М", Purpose = "Санитарная зона", MinAreaSqM = 2, Adjacency = "холл санузлов" },
                new RoomSlot { Name = "Санузел Ж", Purpose = "Санитарная зона", MinAreaSqM = 2, Adjacency = "холл санузлов" },
                new RoomSlot { Name = "Санузел МГН", Purpose = "Санитарная зона", MinAreaSqM = 4.8, Adjacency = "холл санузлов, доступ с зала без препятствий" },
                new RoomSlot { Name = "Кухня / пищеблок", Purpose = "Производство", MinAreaSqM = 25, Adjacency = "зал (раздача), мойка, кладовая" },
                new RoomSlot { Name = "Мойка", Purpose = "Производство", MinAreaSqM = 8, Adjacency = "кухня" },
                new RoomSlot { Name = "Кладовая", Purpose = "Производство", MinAreaSqM = 6, Adjacency = "кухня" },
                new RoomSlot { Name = "Персонал / раздевалка", Purpose = "Персонал", MinAreaSqM = 8, Adjacency = "кухня, служебный вход опц." }
            },
            LayoutLogic =
                "Вход с улицы → тамбур → зал. Санузлы блоком у зала (не в ряд с кухней). " +
                "Кухня + мойка + кладовая — производственный блок с отдельным входом из зала. " +
                "МГН: дверь ≥900 мм, путь из зала без лестниц. Окна — на зал (фасад). " +
                "НЕ линейная «коробка в ряд» — зал крупный, сервис сбоку.",
            DefaultPrompt =
                "Спроектируй кафе на 40 мест на активном плане: зал, тамбур, санузлы М/Ж/МГН, " +
                "кухня, мойка, кладовая, персонал. Стены, двери, окна, марки с площадью, " +
                "цвет по назначению, внутренние размеры. Проверь нормы."
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

        /// <summary>Build AgentInstruction text from a typology definition.</summary>
        public static string BuildAgentInstruction(Typology t)
        {
            if (t == null)
                return "";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("ТИПОЛОГИЯ: " + t.Label + ". " + t.FootprintHint);
            sb.AppendLine("Нормы: query_norm_rules topics=" + string.Join(", ", t.NormTopics) + " — площади только из каталога.");
            sb.AppendLine("ПРОГРАММА ПОМЕЩЕНИЙ:");
            foreach (var r in t.Rooms)
            {
                var area = r.MinAreaSqM.HasValue ? $" ≥{r.MinAreaSqM:0.#} м²" : "";
                sb.AppendLine($"  • {r.Name} ({r.Purpose}){area} — {r.Adjacency}");
            }
            sb.AppendLine("ЛОГИКА: " + t.LayoutLogic);
            sb.AppendLine(TypologyAgentRules);
            return sb.ToString().Trim();
        }

        private const string TypologyAgentRules =
            "СКОРОСТЬ: стены — один create_line_based_element (все сегменты). " +
            "Двери — batch_execute до 4 шт за раз (create_point_based_element, деревянные типы на базовых стенах; НЕ витражные алюминиевые на OST_Walls). " +
            "Помещения — create_room по 2 за вызов. После всех комнат: tag_all_rooms roomIds, color_elements по «Назначение», dimension_room_walls на ключевые. " +
            "Вход с улицы обязателен (hostWallId наружной стены). Окна — на зал/фасад. " +
            "run_norm_audit mode=report в конце. Референс: если есть вложение (скрин/PDF) — ориентируйся на зонирование, не копируй размеры слепо.";
    }
}
