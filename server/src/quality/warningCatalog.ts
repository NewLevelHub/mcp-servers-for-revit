/**
 * Plain-language catalog for Revit's own warnings (REV-180), keyed by
 * FailureDefinitionId — stable across a UI-language flip, unlike the
 * warning's own description text (RU/EN sessions of the same machine are a
 * known occurrence here — see docs/tool-registry.md).
 *
 * Every entry below is grounded in real data: harvested live from a real
 * 35k-element production model (get_model_warnings, then matched by the
 * GUID each occurrence actually carried), not written from memory or a
 * Revit API reference. An unrecognized GUID still gets a fair, honest
 * fallback — the tool must never go silent on a warning type nobody has
 * seen yet.
 *
 * dangerRank is what the ticket calls "сортировка по опасности" — 1 sorts
 * first (duplicate/drifted rooms, silent structural mistakes), 4 last (the
 * everyday, usually-benign case, however many thousand occurrences it has).
 * Raw occurrence count does NOT decide this; a warning that fires 1600
 * times at ordinary wall corners is not more urgent than one that fires
 * once because two Room elements now disagree about a shared floor's area.
 */

export interface WarningCatalogEntry {
  guid: string;
  /** Short, plain name for the group header. */
  title: string;
  /** What it risks at issue time — read first, per the ticket. */
  risk: string;
  /** What to do about it. */
  fix: string;
  dangerRank: 1 | 2 | 3 | 4;
  /** Whether this repo currently ships a working automatic fix for it. */
  autoFixable: boolean;
}

export const WARNING_CATALOG: Record<string, WarningCatalogEntry> = {
  // Дубли/расхождения в помещениях — самое опасное для площадей и спек.
  "83d4a67c-818c-4291-adaf-f2d33064fea8": {
    guid: "83d4a67c-818c-4291-adaf-f2d33064fea8",
    title: "Два помещения в одном контуре",
    risk:
      "Площадь и периметр достанутся только одному из помещений — у второго в ведомости будет " +
      "0 м² или «Избыточная». Если оба используются в ТЭП/спеке, там уже ошибка.",
    fix:
      "Определите, какое помещение лишнее: удалите его или перенесите в свой контур. Auto-fix " +
      "не делает этого сам — удаление не того элемента стирает его имя/номер безвозвратно.",
    dangerRank: 1,
    autoFixable: false,
  },
  "a22de05c-4c92-4bdc-9ce3-a965d2cf316c": {
    guid: "a22de05c-4c92-4bdc-9ce3-a965d2cf316c",
    title: "Объёмы помещений перекрываются",
    risk: "Площадь/объём одного из помещений посчитан неверно — тянется в ТЭП и спецификации.",
    fix: "Проверьте «Верхний предел» и «Смещение предела» у обоих помещений на этом месте.",
    dangerRank: 1,
    autoFixable: false,
  },
  "4f0bba25-e17f-480a-a763-d97d184be18a": {
    guid: "4f0bba25-e17f-480a-a763-d97d184be18a",
    title: "Марка помещения вне помещения",
    risk: "Видно на любом выпущенном листе — марка висит в воздухе, не над своим помещением.",
    fix: "Включите выноску у марки или перетащите её обратно внутрь контура помещения.",
    dangerRank: 1,
    autoFixable: false,
  },

  // Тихие геометрические ошибки — не видны на плане, но реальны в разрезе/3D или в конструктиве.
  "9e039ed2-44c3-4f24-b836-27068f12c4e5": {
    guid: "9e039ed2-44c3-4f24-b836-27068f12c4e5",
    title: "Прямоугольный проём не режет основу",
    risk:
      "На плане проём нарисован, а стена под ним на самом деле сплошная — трасса, которая должна " +
      "была пройти здесь, упрётся в материал, которого «не должно быть».",
    fix: "Проверьте тип основы и положение проёма — пересоздайте, если геометрия не совпала.",
    dangerRank: 2,
    autoFixable: false,
  },
  "18fc24c5-afa3-4b15-aefc-021f02f92695": {
    guid: "18fc24c5-afa3-4b15-aefc-021f02f92695",
    title: "Стена привязана к цели, которой больше нет",
    risk:
      "«Прикрепить сверху/снизу» стоит, но целевой элемент (перекрытие, крыша) стена больше не " +
      "находит — высота стены могла тихо остаться прежней там, где перекрытие уже переместили.",
    fix: "Откройте разрез в этом месте и перепривяжите стену к нужному элементу заново.",
    dangerRank: 2,
    autoFixable: false,
  },

  // Документация: дубли марок портят спецификации напрямую.
  "6e1efefe-c8e0-483d-8482-150b9f1da21a": {
    guid: "6e1efefe-c8e0-483d-8482-150b9f1da21a",
    title: "Повторяющаяся марка/маркировка типоразмера",
    risk: "В спецификации две строки с одинаковой маркой — на площадке не понять, какая деталь где.",
    fix: "Переномеруйте один из повторов по своей схеме маркировки.",
    dangerRank: 2,
    autoFixable: false,
  },

  // Redundant room-separation line — ticket's own named example, and the one auto-fix in v1.
  "f7b3a015-c3eb-4a3f-b345-c474ec07d43f": {
    guid: "f7b3a015-c3eb-4a3f-b345-c474ec07d43f",
    title: "Линия-разделитель помещений поверх стены",
    risk:
      "Обычно безвредно: стена и так задаёт границу помещения, линия-разделитель здесь лишняя.",
    fix: "Удалите линию-разделитель на этом участке — стена продолжит держать границу сама.",
    dangerRank: 2,
    autoFixable: true,
  },

  // Пересечения/вложенность — часто требуют геометрической оценки, не однозначны для авточинки.
  "8695a52f-2a88-4ca2-bedc-3676d5857af6": {
    guid: "8695a52f-2a88-4ca2-bedc-3676d5857af6",
    title: "Перекрытия пересекаются",
    risk: "Двойной учёт площади/материала на пересечении в спеках и подсчёте объёмов.",
    fix: "Обрежьте одно из перекрытий по границе другого (Join Geometry или правка контура).",
    dangerRank: 3,
    autoFixable: false,
  },
  "505d84a1-67e4-4987-8287-21ad1792ffe9": {
    guid: "505d84a1-67e4-4987-8287-21ad1792ffe9",
    title: "Один элемент целиком внутри другого",
    risk:
      "Может быть настоящий дубль стены — а может быть намеренная вложенная конструкция. " +
      "Не одно и то же, разница видна только глазами.",
    fix: "Откройте оба элемента через TAB и решите на месте: дубль — удалить, задумано — оставить.",
    dangerRank: 3,
    autoFixable: false,
  },

  // Самые частые, обычно безобидные — в конец списка, как «слегка вне оси» в примере тикета.
  "7240576f-66ca-40e7-bc79-be5af5f891f5": {
    guid: "7240576f-66ca-40e7-bc79-be5af5f891f5",
    title: "Конфликт с соседней стеной при вставке",
    risk: "Обычно косметика в месте стыка — но иногда элемент реально встал не там, где нужно.",
    fix: "Проверьте элемент в 3D на этом стыке; чаще всего можно просто закрыть предупреждение.",
    dangerRank: 4,
    autoFixable: false,
  },
  "988a6cb2-7050-4a5c-a946-60d652df66c3": {
    guid: "988a6cb2-7050-4a5c-a946-60d652df66c3",
    title: "Стены перекрываются",
    risk:
      "Самый частый Revit-warning — в местах стыка стен это нормальное поведение, не ошибка " +
      "моделирования. Разбирать стоит только там, где стены НЕ должны встречаться.",
    fix: "«Разрешить вырезание геометрии» на реальном стыке; в остальных местах можно игнорировать.",
    dangerRank: 4,
    autoFixable: false,
  },
};

/** Never auto-fixed even if a future entry above is mis-flagged — belt and suspenders (REV-180). */
export const NEVER_AUTO_FIX_GUIDS = new Set<string>(
  Object.values(WARNING_CATALOG)
    .filter((e) => !e.autoFixable)
    .map((e) => e.guid)
);

const FALLBACK_RANK = 3;

export interface ExplainedWarning {
  guid: string;
  title: string;
  risk: string;
  fix: string;
  dangerRank: number;
  autoFixable: boolean;
  /** True when the GUID wasn't in the catalog — description is Revit's own raw text instead. */
  uncatalogued: boolean;
}

/** Never throws — an unrecognized GUID still gets a fair, honest fallback, never silence. */
export function explainWarning(guid: string, fallbackDescription: string): ExplainedWarning {
  const entry = guid ? WARNING_CATALOG[guid] : undefined;
  if (entry) {
    return { ...entry, uncatalogued: false };
  }
  return {
    guid,
    title: "Нестандартное предупреждение Revit",
    risk: "Не в каталоге — оценить риск может только текст самого Revit ниже.",
    fix: fallbackDescription || "Текст предупреждения не передан.",
    dangerRank: FALLBACK_RANK,
    autoFixable: false,
    uncatalogued: true,
  };
}
