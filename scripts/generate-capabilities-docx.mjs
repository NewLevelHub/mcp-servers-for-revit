/**
 * Generates docs/Revit-AI-bot-vozmozhnosti.docx — client-facing capability overview.
 * Run: node scripts/generate-capabilities-docx.mjs
 */
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";
import {
  AlignmentType,
  BorderStyle,
  Document,
  Footer,
  Header,
  HeadingLevel,
  Packer,
  PageNumber,
  Paragraph,
  ShadingType,
  Table,
  TableCell,
  TableRow,
  TextRun,
  VerticalAlign,
  WidthType,
  convertInchesToTwip,
} from "docx";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(__dirname, "..");
const outPath = path.join(root, "docs", "Revit-AI-bot-vozmozhnosti.docx");

// Architectural palette: deep navy + steel + soft gray (no purple / cream-terracotta)
const C = {
  navy: "1A2744",
  steel: "2F5D8A",
  accent: "3D7EA6",
  soft: "E8EEF4",
  softAlt: "F4F7FA",
  line: "C5D0DC",
  text: "1F2933",
  muted: "5B6B7C",
  white: "FFFFFF",
  ok: "1F6B4A",
  warnBg: "FFF8E8",
  warnBorder: "D4A017",
};

const PAGE_W = 11906; // A4 twips approx via DXA: 210mm
const MARGIN = convertInchesToTwip(0.7);
const CONTENT_W = 9360; // usable table width in DXA

function hr() {
  return new Paragraph({
    spacing: { before: 120, after: 200 },
    border: {
      bottom: { style: BorderStyle.SINGLE, size: 12, color: C.line, space: 1 },
    },
    children: [],
  });
}

function p(text, opts = {}) {
  const {
    bold = false,
    size = 20,
    color = C.text,
    center = false,
    after = 80,
    before = 0,
    italics = false,
  } = opts;
  return new Paragraph({
    alignment: center ? AlignmentType.CENTER : AlignmentType.LEFT,
    spacing: { after, before, line: 276 },
    children: [
      new TextRun({
        text,
        bold,
        italics,
        size,
        color,
        font: "Calibri",
      }),
    ],
  });
}

function heading(text, level = HeadingLevel.HEADING_1) {
  const isH1 = level === HeadingLevel.HEADING_1;
  return new Paragraph({
    heading: level,
    spacing: { before: isH1 ? 280 : 200, after: 120 },
    children: [
      new TextRun({
        text,
        bold: true,
        size: isH1 ? 28 : 22,
        color: C.navy,
        font: "Calibri",
      }),
    ],
  });
}

function bullet(text) {
  return new Paragraph({
    spacing: { after: 60, line: 276 },
    indent: { left: 360 },
    children: [
      new TextRun({ text: "•  ", color: C.accent, size: 20, font: "Calibri" }),
      new TextRun({ text, color: C.text, size: 20, font: "Calibri" }),
    ],
  });
}

function cell(text, opts = {}) {
  const {
    bold = false,
    header = false,
    width = CONTENT_W / 2,
    color = C.text,
    fill,
    center = false,
  } = opts;
  const bg = fill ?? (header ? C.navy : undefined);
  return new TableCell({
    width: { size: width, type: WidthType.DXA },
    shading: bg
      ? { type: ShadingType.CLEAR, fill: bg }
      : undefined,
    verticalAlign: VerticalAlign.CENTER,
    margins: {
      top: 60,
      bottom: 60,
      left: 100,
      right: 100,
    },
    children: [
      new Paragraph({
        alignment: center ? AlignmentType.CENTER : AlignmentType.LEFT,
        spacing: { after: 0 },
        children: [
          new TextRun({
            text,
            bold: bold || header,
            size: header ? 18 : 18,
            color: header ? C.white : color,
            font: "Calibri",
          }),
        ],
      }),
    ],
  });
}

function twoColTable(
  rows,
  col1 = 3200,
  col2 = CONTENT_W - 3200,
  headers = ["Что просите", "Что появляется в Revit"]
) {
  const header = new TableRow({
    children: [
      cell(headers[0], { header: true, width: col1, bold: true }),
      cell(headers[1], { header: true, width: col2, bold: true }),
    ],
  });
  const body = rows.map(([a, b], i) =>
    new TableRow({
      children: [
        cell(a, {
          bold: true,
          width: col1,
          fill: i % 2 ? C.softAlt : C.white,
          color: C.navy,
        }),
        cell(b, {
          width: col2,
          fill: i % 2 ? C.softAlt : C.white,
        }),
      ],
    })
  );
  return new Table({
    width: { size: CONTENT_W, type: WidthType.DXA },
    columnWidths: [col1, col2],
    rows: [header, ...body],
  });
}

function mapCard(title, lines) {
  const w = Math.floor(CONTENT_W / 3) - 40;
  return new TableCell({
    width: { size: w, type: WidthType.DXA },
    shading: { type: ShadingType.CLEAR, fill: C.soft },
    borders: {
      top: { style: BorderStyle.SINGLE, size: 1, color: C.line },
      bottom: { style: BorderStyle.SINGLE, size: 1, color: C.line },
      left: { style: BorderStyle.SINGLE, size: 1, color: C.line },
      right: { style: BorderStyle.SINGLE, size: 1, color: C.line },
    },
    margins: { top: 80, bottom: 80, left: 100, right: 100 },
    children: [
      new Paragraph({
        spacing: { after: 60 },
        border: {
          bottom: { style: BorderStyle.SINGLE, size: 8, color: C.accent, space: 4 },
        },
        children: [
          new TextRun({
            text: title,
            bold: true,
            size: 18,
            color: C.navy,
            font: "Calibri",
          }),
        ],
      }),
      ...lines.map(
        (line) =>
          new Paragraph({
            spacing: { after: 40 },
            children: [
              new TextRun({
                text: "·  " + line,
                size: 16,
                color: C.muted,
                font: "Calibri",
              }),
            ],
          })
      ),
    ],
  });
}

function emptyCell(w) {
  return new TableCell({
    width: { size: w, type: WidthType.DXA },
    borders: {
      top: { style: BorderStyle.NONE },
      bottom: { style: BorderStyle.NONE },
      left: { style: BorderStyle.NONE },
      right: { style: BorderStyle.NONE },
    },
    children: [new Paragraph({ children: [] })],
  });
}

function flowStep(n, title, sub, w) {
  return new TableCell({
    width: { size: w, type: WidthType.DXA },
    shading: { type: ShadingType.CLEAR, fill: C.navy },
    margins: { top: 80, bottom: 80, left: 80, right: 80 },
    children: [
      new Paragraph({
        alignment: AlignmentType.CENTER,
        spacing: { after: 40 },
        children: [
          new TextRun({
            text: String(n),
            bold: true,
            size: 28,
            color: C.accent,
            font: "Calibri",
          }),
        ],
      }),
      new Paragraph({
        alignment: AlignmentType.CENTER,
        spacing: { after: 20 },
        children: [
          new TextRun({
            text: title,
            bold: true,
            size: 16,
            color: C.white,
            font: "Calibri",
          }),
        ],
      }),
      new Paragraph({
        alignment: AlignmentType.CENTER,
        children: [
          new TextRun({
            text: sub,
            size: 14,
            color: "A8C0D8",
            font: "Calibri",
          }),
        ],
      }),
    ],
  });
}

function callout(title, bodyLines) {
  return new Table({
    width: { size: CONTENT_W, type: WidthType.DXA },
    columnWidths: [CONTENT_W],
    rows: [
      new TableRow({
        children: [
          new TableCell({
            width: { size: CONTENT_W, type: WidthType.DXA },
            shading: { type: ShadingType.CLEAR, fill: C.warnBg },
            borders: {
              top: { style: BorderStyle.SINGLE, size: 8, color: C.warnBorder },
              bottom: { style: BorderStyle.SINGLE, size: 8, color: C.warnBorder },
              left: { style: BorderStyle.SINGLE, size: 24, color: C.warnBorder },
              right: { style: BorderStyle.SINGLE, size: 8, color: C.warnBorder },
            },
            margins: { top: 100, bottom: 100, left: 140, right: 140 },
            children: [
              new Paragraph({
                spacing: { after: 80 },
                children: [
                  new TextRun({
                    text: title,
                    bold: true,
                    size: 20,
                    color: C.navy,
                    font: "Calibri",
                  }),
                ],
              }),
              ...bodyLines.map(
                (t) =>
                  new Paragraph({
                    spacing: { after: 40 },
                    children: [
                      new TextRun({
                        text: t,
                        size: 18,
                        color: C.text,
                        font: "Calibri",
                      }),
                    ],
                  })
              ),
            ],
          }),
        ],
      }),
    ],
  });
}

function quoteBlock(lines) {
  return lines.map(
    (t) =>
      new Paragraph({
        spacing: { after: 60 },
        indent: { left: 200 },
        border: {
          left: { style: BorderStyle.SINGLE, size: 18, color: C.accent, space: 10 },
        },
        children: [
          new TextRun({
            text: t,
            italics: true,
            size: 18,
            color: C.muted,
            font: "Calibri",
          }),
        ],
      })
  );
}

const gap = () =>
  new Paragraph({ spacing: { after: 120 }, children: [] });

const doc = new Document({
  styles: {
    default: {
      document: {
        styles: [
          {
            id: "Normal",
            run: { font: "Calibri", size: 20, color: C.text },
          },
        ],
      },
    },
  },
  sections: [
    {
      properties: {
        page: {
          margin: {
            top: MARGIN,
            bottom: MARGIN,
            left: MARGIN,
            right: MARGIN,
          },
        },
      },
      headers: {
        default: new Header({
          children: [
            new Paragraph({
              spacing: { after: 60 },
              border: {
                bottom: {
                  style: BorderStyle.SINGLE,
                  size: 12,
                  color: C.steel,
                  space: 6,
                },
              },
              children: [
                new TextRun({
                  text: "Revit AI-бот",
                  bold: true,
                  size: 16,
                  color: C.navy,
                  font: "Calibri",
                }),
                new TextRun({
                  text: "  ·  возможности для архитекторов и ГИП",
                  size: 16,
                  color: C.muted,
                  font: "Calibri",
                }),
              ],
            }),
          ],
        }),
      },
      footers: {
        default: new Footer({
          children: [
            new Paragraph({
              alignment: AlignmentType.CENTER,
              border: {
                top: {
                  style: BorderStyle.SINGLE,
                  size: 6,
                  color: C.line,
                  space: 8,
                },
              },
              children: [
                new TextRun({
                  text: "Точка отсчёта для совместной работы  ·  стр. ",
                  size: 14,
                  color: C.muted,
                  font: "Calibri",
                }),
                new TextRun({
                  children: [PageNumber.CURRENT],
                  size: 14,
                  color: C.muted,
                  font: "Calibri",
                }),
              ],
            }),
          ],
        }),
      },
      children: [
        // Cover band
        new Table({
          width: { size: CONTENT_W, type: WidthType.DXA },
          columnWidths: [CONTENT_W],
          rows: [
            new TableRow({
              children: [
                new TableCell({
                  width: { size: CONTENT_W, type: WidthType.DXA },
                  shading: { type: ShadingType.CLEAR, fill: C.navy },
                  margins: {
                    top: 200,
                    bottom: 200,
                    left: 200,
                    right: 200,
                  },
                  borders: {
                    top: { style: BorderStyle.NONE },
                    bottom: { style: BorderStyle.NONE },
                    left: { style: BorderStyle.NONE },
                    right: { style: BorderStyle.NONE },
                  },
                  children: [
                    new Paragraph({
                      spacing: { after: 60 },
                      children: [
                        new TextRun({
                          text: "REVIT AI-БОТ",
                          bold: true,
                          size: 40,
                          color: C.white,
                          font: "Calibri",
                        }),
                      ],
                    }),
                    new Paragraph({
                      spacing: { after: 120 },
                      children: [
                        new TextRun({
                          text: "Что умеет делать в проекте",
                          size: 26,
                          color: "A8C0D8",
                          font: "Calibri",
                        }),
                      ],
                    }),
                    new Paragraph({
                      children: [
                        new TextRun({
                          text: "Краткий обзор для архитекторов и ГИП. Без технической настройки — только возможности и результат в модели.",
                          size: 18,
                          color: "D6E2EE",
                          font: "Calibri",
                        }),
                      ],
                    }),
                  ],
                }),
              ],
            }),
          ],
        }),
        gap(),
        p(
          "Бот работает в открытом .rvt на шаблоне организации. Запросы — обычным языком; результат появляется сразу в Revit.",
          { size: 20, after: 160 }
        ),

        heading("1. Карта возможностей"),
        p("Семь блоков текущей поставки:", { color: C.muted, after: 120 }),

        // Row 1 of map cards
        new Table({
          width: { size: CONTENT_W, type: WidthType.DXA },
          columnWidths: [
            Math.floor(CONTENT_W / 3) - 40,
            60,
            Math.floor(CONTENT_W / 3) - 40,
            60,
            Math.floor(CONTENT_W / 3) - 40,
          ],
          rows: [
            new TableRow({
              children: [
                mapCard("МОДЕЛЬ", [
                  "Стены, полы, крыши",
                  "Двери, окна, уровни",
                  "Лестницы, ограждения",
                  "Проёмы / шахты",
                ]),
                emptyCell(60),
                mapCard("ПОМЕЩЕНИЯ", [
                  "Rooms + марки",
                  "Нумерация",
                  "Цвета, квартирография",
                  "Выгрузка данных",
                ]),
                emptyCell(60),
                mapCard("ОСИ И РАЗМЕРЫ", [
                  "Grids по несущим",
                  "Осевые цепочки",
                  "Размеры комнат",
                  "Разрез по элементу",
                ]),
              ],
            }),
          ],
        }),
        gap(),
        new Table({
          width: { size: CONTENT_W, type: WidthType.DXA },
          columnWidths: [
            Math.floor(CONTENT_W / 3) - 40,
            60,
            Math.floor(CONTENT_W / 3) - 40,
            60,
            Math.floor(CONTENT_W / 3) - 40,
          ],
          rows: [
            new TableRow({
              children: [
                mapCard("ДОКУМЕНТАЦИЯ", [
                  "Спеки, экспликации",
                  "Лист · план · раскладка",
                  "Штамп, ТЭП",
                  "Сверка с моделью",
                ]),
                emptyCell(60),
                mapCard("АНАЛИТИКА + НОРМЫ", [
                  "Площади, материалы",
                  "Аудит этажа",
                  "Заливка нарушений",
                  "Цитаты ГОСТ / СП",
                ]),
                emptyCell(60),
                mapCard("УЗЛЫ (ПИЛОТ)", [
                  "Чертёжный вид",
                  "Эскиз слоёв",
                  "Подписи, размеры",
                  "Лист узла",
                ]),
              ],
            }),
          ],
        }),

        heading("2. Типовой сценарий на плане"),
        p("От геометрии до оформления — цепочка, которую бот закрывает сегодня:", {
          color: C.muted,
          after: 120,
        }),
        (() => {
          const w = Math.floor(CONTENT_W / 5);
          return new Table({
            width: { size: CONTENT_W, type: WidthType.DXA },
            columnWidths: Array(5).fill(w),
            rows: [
              new TableRow({
                children: [
                  flowStep(1, "Модель", "стены · проёмы", w),
                  flowStep(2, "Rooms", "марки · №", w),
                  flowStep(3, "Аннотации", "оси · размеры", w),
                  flowStep(4, "Листы", "спеки · штамп", w),
                  flowStep(5, "Нормы", "аудит · заливка", w),
                ],
              }),
            ],
          });
        })(),

        heading("3. Моделирование"),
        gap(),
        twoColTable([
          ["Стены, балки", "Линейные элементы по типу из проекта"],
          ["Полы, потолки, крыши", "Поверхностные элементы по контуру"],
          ["Двери, окна", "Семейства в стене-хосте"],
          ["Уровни", "Levels на заданных отметках"],
          ["Несущий каркас", "Система балок по области с шагом"],
          ["Лестницы", "Прямая / Г / П (в т.ч. в шахте типового этажа)"],
          ["Ограждения", "По пути или на лестнице (тип из проекта)"],
          ["Проём в плите / шахта", "Вырез в перекрытии или шахта между уровнями"],
          ["Удалить / скрыть / цвет", "Операции над элементами модели"],
        ]),
        gap(),
        p(
          "Типы семейств берутся из проекта. Без типа в шаблоне элемент не создаётся «наугад».",
          { italics: true, color: C.muted, size: 18 }
        ),

        heading("4. Помещения"),
        gap(),
        twoColTable([
          ["Помещения", "Rooms внутри замкнутого контура"],
          ["Марки помещений", "Room Tags (в т.ч. с площадью)"],
          ["Марки стен", "Wall Tags"],
          [
            "Нумерация",
            "101 / 201 по этажу или сквозная; змейка / по кругу; квартиры и секции",
          ],
          ["Цвета", "Цветовая схема вида по имени / назначению"],
          ["Подсветка площади", "Цветовые области (Filled Region)"],
          ["Выгрузка", "Имя, номер, уровень, площадь, объём, отделка"],
          ["Квартирография", "Группировка Rooms по номеру квартиры"],
        ]),
        gap(),
        p(
          "Нумерация: по умолчанию сначала preview (старый → новый), в модель — после подтверждения.",
          { italics: true, color: C.muted, size: 18 }
        ),

        heading("5. Оси и размеры"),
        gap(),
        twoColTable([
          [
            "Координационные оси",
            "Grids по ЦЛ несущих стен; пузыри цифры / буквы",
          ],
          ["Настройка осей", "Экстенты и тип марки без пересоздания"],
          [
            "Осевые размеры",
            "Внешние цепочки от габарита здания (межосевой + габарит)",
          ],
          ["Размеры помещений", "Внутренние цепочки ширины × глубины"],
          ["Произвольные размеры", "Цепочки между элементами / точками"],
          ["Текст с выноской", "Text Notes + leader"],
          [
            "Разрез / фасад по элементу",
            "Section / Elevation, кадрированный на элемент (напр. лестница)",
          ],
        ]),
        gap(),
        p("Оформление плана — схема", { bold: true, size: 20, color: C.navy }),
        bullet(
          "Снаружи: габарит корпуса → межосевой ярус → габаритный ярус → пузыри осей"
        ),
        bullet(
          "Внутри комнат: ширина × глубина (+ при необходимости привязки проёмов)"
        ),
        p(
          "Типы размеров, осей и текста — из шаблона проекта (ADSK и др.).",
          { italics: true, color: C.muted, size: 18, before: 80 }
        ),

        heading("6. Ведомости, ТЭП, листы"),
        p("Листы и компоновка", {
          bold: true,
          size: 22,
          color: C.steel,
          before: 40,
        }),
        p(
          "Бот создаёт лист из шаблона проекта, ставит планы и спецификации, умеет автокомпоновку без ручных координат.",
          { color: C.muted, after: 120 }
        ),
        gap(),
        twoColTable([
          ["Создать лист", "Sheet с title block (рамкой) из проекта"],
          ["Разместить план", "Viewport: план этажа / вид на лист"],
          ["Разместить спецификацию", "ScheduleSheetInstance на лист"],
          [
            "Автокомпоновка",
            "Ряды в рабочей зоне; поля ГОСТ и штамп; подгонка масштаба плана",
          ],
          ["Основная надпись", "Заполнение штампа (СПДС/ГОСТ) полями проекта"],
        ]),
        gap(),
        p("Спецификации и данные", {
          bold: true,
          size: 22,
          color: C.steel,
          before: 80,
        }),
        gap(),
        twoColTable([
          ["Спеки дверей / окон", "Schedule (блоки без откосов)"],
          ["Экспликация полов", "Только отделка (полы)*, площади м²"],
          [
            "Лист экспликации полов",
            "Спеки по группам этажей + лист + раскладка",
          ],
          ["Ведомость отделки", "Цепочка спек по шаблону ADSK"],
          ["Витражи", "Данные / спека curtain wall systems"],
          ["ТЭП", "Выгрузка показателей; таблица на листе"],
          ["Сверка спеки", "Отчёт расхождений с моделью"],
          ["Материалы", "Площадь, объём, число элементов"],
        ]),

        heading("7. Нормоконтроль"),
        p(
          "Библиотека ГОСТ / СП / СН РК. Числа не выдумываются — только из документов с цитатой.",
          { color: C.muted, after: 120 }
        ),
        (() => {
          const w = Math.floor(CONTENT_W / 4);
          return new Table({
            width: { size: CONTENT_W, type: WidthType.DXA },
            columnWidths: Array(4).fill(w),
            rows: [
              new TableRow({
                children: [
                  flowStep(1, "Нормы", "библиотека", w),
                  flowStep(2, "Аудит", "сводный отчёт", w),
                  flowStep(3, "Заливка", "Filled Region", w),
                  flowStep(4, "Подписи", "текст + выноска", w),
                ],
              }),
            ],
          });
        })(),
        gap(),
        twoColTable([
          ["Спросить норму", "Документ, пункт, цитата"],
          [
            "Проверить этаж",
            "Коридоры, глубины, лоджии/балконы/простенки, ПД, двери, площади/высоты, проёмы, лестницы/пандусы, тамбуры, МГН",
          ],
          ["Показать нарушения", "Заливка помещений + цвет дверей"],
          ["Подписать нарушения", "Текст с выноской к элементу"],
          ["Записать статус", "Параметры / марки в модели"],
        ]),
        gap(),
        callout("Важно", [
          "Нет правила в библиотеке → проверка помечается как «пропущена», а не «всё ОК».",
        ]),

        heading("8. Узлы РД (пилот)"),
        p(
          "Механическое оформление узла, когда решение уже принято архитектором или взято из эталона.",
          { color: C.muted, after: 120 }
        ),
        gap(),
        twoColTable([
          ["Чертёжный вид / callout", "Detail / drafting view"],
          ["Эскиз слоёв", "Detail lines (без новых .rfa)"],
          ["Подписи", "Текст с выносками"],
          ["Размеры", "Толщины, зазоры"],
          ["2D-детали из библиотеки", "Если семейство уже в проекте"],
          ["Лист", "Viewport узла на лист организации"],
        ]),
        gap(),
        callout("Формула роли", [
          "Решение «какой узел» → архитектор  ·  эталон и .rfa → шаблон организации",
          "Вид / эскиз / подписи / размеры / лист → ИИ  ·  приёмка и выпуск → архитектор",
          "Пилот пройден на публичном эталоне «примыкание пола к стене». Под ваш шаблон — после 1–2 эталонных узлов.",
        ]),

        heading("9. Чтение модели"),
        bullet("Активный вид (тип, имя, масштаб, уровень)"),
        bullet("Элементы на виде и выделение"),
        bullet("Параметры любого элемента"),
        bullet("Типы семейств в проекте"),
        bullet("Стили: размеры, оси, текст, штампы"),
        bullet("Статистика модели, геометрия для проверок"),

        heading("10. Границы и зависимости"),
        gap(),
        twoColTable(
          [
            [
              "Действия в открытой модели по запросу",
              "Выбор конструктива и ответственность за выпуск — архитектор / ГИП",
            ],
            [
              "Типы, марки, штампы из вашего шаблона",
              "Поставка шаблона и эталонов — организация",
            ],
            [
              "Нормы с цитатой из библиотеки",
              "Какие документы обязательны на объекте — заказчик",
            ],
            [
              "Черновик узла по эталону",
              "Приёмка узла и альбома РД — архитектор",
            ],
          ],
          4200,
          CONTENT_W - 4200,
          ["ИИ делает", "Архитектор / организация"]
        ),
        gap(),
        p("Не делает:", { bold: true, size: 20, color: C.navy, before: 80 }),
        bullet("Новый .rvt «с нуля» без шаблона организации"),
        bullet("Выдуманные нормативные значения"),
        bullet("Самостоятельный выбор конструктивного узла"),
        bullet("Замена ГИПа на выпуске документации"),

        heading("11. Примеры формулировок"),
        p("Пишете обычным языком — бот сам выбирает команды:", {
          color: C.muted,
          after: 100,
        }),
        ...quoteBlock([
          "«На этом этаже оси по несущим, размеры снаружи и внутри комнат»",
          "«Поставь Rooms и марки с площадью»",
          "«Пронумеруй помещения: 101, 102… по этажам, змейкой; сначала покажи план»",
          "«Сделай экспликацию полов и лист А2»",
          "«Создай лист, поставь план 1 этажа и спеки, разложи автокомпоновкой»",
          "«Заполни штамп на листе»",
          "«Спеки дверей и окон, сверь с моделью»",
          "«Выгрузи ТЭП и квартирографию»",
          "«Поставь лестницу П в шахту между уровнями и ограждение»",
          "«Проверь этаж по нормам, покажи нарушения заливкой и подпиши»",
          "«Набросай узел примыкания пола к стене М1:10 на чертёжном виде и вынеси на лист»",
        ]),

        hr(),
        new Table({
          width: { size: CONTENT_W, type: WidthType.DXA },
          columnWidths: [CONTENT_W],
          rows: [
            new TableRow({
              children: [
                new TableCell({
                  width: { size: CONTENT_W, type: WidthType.DXA },
                  shading: { type: ShadingType.CLEAR, fill: C.soft },
                  margins: {
                    top: 120,
                    bottom: 120,
                    left: 140,
                    right: 140,
                  },
                  borders: {
                    top: { style: BorderStyle.SINGLE, size: 1, color: C.line },
                    bottom: { style: BorderStyle.SINGLE, size: 1, color: C.line },
                    left: { style: BorderStyle.SINGLE, size: 1, color: C.line },
                    right: { style: BorderStyle.SINGLE, size: 1, color: C.line },
                  },
                  children: [
                    new Paragraph({
                      spacing: { after: 60 },
                      children: [
                        new TextRun({
                          text: "Точка отсчёта",
                          bold: true,
                          size: 20,
                          color: C.navy,
                          font: "Calibri",
                        }),
                      ],
                    }),
                    new Paragraph({
                      children: [
                        new TextRun({
                          text: "Контур план → помещения → размеры → спеки/листы → нормы готов. Дальше — приоритеты заказчика (типовой этаж, узлы, расширенный нормоконтроль) на вашем шаблоне.",
                          size: 18,
                          color: C.text,
                          font: "Calibri",
                        }),
                      ],
                    }),
                  ],
                }),
              ],
            }),
          ],
        }),
      ],
    },
  ],
});

const buffer = await Packer.toBuffer(doc);
fs.writeFileSync(outPath, buffer);
console.log("Wrote", outPath);
