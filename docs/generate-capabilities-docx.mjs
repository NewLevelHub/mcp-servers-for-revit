import {
  Document,
  Packer,
  Paragraph,
  TextRun,
  Table,
  TableRow,
  TableCell,
  WidthType,
  BorderStyle,
  AlignmentType,
  ShadingType,
  VerticalAlign,
  Header,
  Footer,
  PageNumber,
  HeadingLevel,
} from "docx";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

const C = {
  ink: "1A1A1A",
  muted: "5C5C5C",
  line: "D0D0D0",
  white: "FFFFFF",
  soft: "F4F4F2",
  accent: "1F4E79",
  accentSoft: "E8F0F7",
  green: "2E5A3C",
  greenSoft: "E8F2EC",
  amber: "8A5A00",
  amberSoft: "F7F0E0",
  purple: "4A3A6B",
  purpleSoft: "F0ECF5",
  teal: "1A5F5A",
  tealSoft: "E6F2F1",
  red: "7A2E2E",
  redSoft: "F5EAEA",
};

const noBorder = {
  top: { style: BorderStyle.NONE, size: 0, color: "FFFFFF" },
  bottom: { style: BorderStyle.NONE, size: 0, color: "FFFFFF" },
  left: { style: BorderStyle.NONE, size: 0, color: "FFFFFF" },
  right: { style: BorderStyle.NONE, size: 0, color: "FFFFFF" },
};

const thinBorder = {
  top: { style: BorderStyle.SINGLE, size: 4, color: C.line },
  bottom: { style: BorderStyle.SINGLE, size: 4, color: C.line },
  left: { style: BorderStyle.SINGLE, size: 4, color: C.line },
  right: { style: BorderStyle.SINGLE, size: 4, color: C.line },
};

function cell(text, opts = {}) {
  const {
    bold = false,
    fill = C.white,
    color = C.ink,
    width = 4680,
    align = AlignmentType.LEFT,
    borders = thinBorder,
    fontSize = 18,
  } = opts;
  return new TableCell({
    width: { size: width, type: WidthType.DXA },
    borders,
    shading: { type: ShadingType.CLEAR, fill },
    verticalAlign: VerticalAlign.CENTER,
    children: [
      new Paragraph({
        alignment: align,
        spacing: { before: 80, after: 80 },
        children: [
          new TextRun({
            text,
            bold,
            color,
            size: fontSize,
            font: "Calibri",
          }),
        ],
      }),
    ],
  });
}

function pairTable(rows, col1Fill = C.soft, col2Fill = C.white) {
  const w1 = 3600;
  const w2 = 5760;
  return new Table({
    width: { size: 9360, type: WidthType.DXA },
    columnWidths: [w1, w2],
    rows: [
      new TableRow({
        children: [
          cell("Что просите", {
            bold: true,
            fill: C.accent,
            color: C.white,
            width: w1,
            align: AlignmentType.CENTER,
          }),
          cell("Что появляется в Revit", {
            bold: true,
            fill: C.accent,
            color: C.white,
            width: w2,
            align: AlignmentType.CENTER,
          }),
        ],
      }),
      ...rows.map(
        ([a, b], i) =>
          new TableRow({
            children: [
              cell(a, {
                bold: true,
                fill: i % 2 ? col1Fill : C.white,
                width: w1,
                color: C.ink,
              }),
              cell(b, {
                fill: i % 2 ? col2Fill : C.white,
                width: w2,
                color: C.muted,
              }),
            ],
          })
      ),
    ],
  });
}

function moduleCard(title, items, fill, accent) {
  const w = 3000;
  return new TableCell({
    width: { size: w, type: WidthType.DXA },
    borders: thinBorder,
    shading: { type: ShadingType.CLEAR, fill },
    children: [
      new Paragraph({
        spacing: { before: 120, after: 60 },
        alignment: AlignmentType.CENTER,
        children: [
          new TextRun({
            text: title,
            bold: true,
            color: accent,
            size: 20,
            font: "Calibri",
          }),
        ],
      }),
      ...items.map(
        (t) =>
          new Paragraph({
            spacing: { before: 40, after: 40 },
            indent: { left: 100 },
            children: [
              new TextRun({
                text: "•  " + t,
                color: C.ink,
                size: 16,
                font: "Calibri",
              }),
            ],
          })
      ),
      new Paragraph({ children: [] }),
    ],
  });
}

function flowStep(n, title, subtitle, fill, accent, width = 2100) {
  return new TableCell({
    width: { size: width, type: WidthType.DXA },
    borders: thinBorder,
    shading: { type: ShadingType.CLEAR, fill },
    verticalAlign: VerticalAlign.CENTER,
    children: [
      new Paragraph({
        alignment: AlignmentType.CENTER,
        spacing: { before: 100 },
        children: [
          new TextRun({
            text: String(n),
            bold: true,
            color: accent,
            size: 28,
            font: "Calibri",
          }),
        ],
      }),
      new Paragraph({
        alignment: AlignmentType.CENTER,
        spacing: { before: 40 },
        children: [
          new TextRun({
            text: title,
            bold: true,
            color: C.ink,
            size: 17,
            font: "Calibri",
          }),
        ],
      }),
      new Paragraph({
        alignment: AlignmentType.CENTER,
        spacing: { before: 40, after: 100 },
        children: [
          new TextRun({
            text: subtitle,
            color: C.muted,
            size: 14,
            font: "Calibri",
          }),
        ],
      }),
    ],
  });
}

function arrowCell() {
  return new TableCell({
    width: { size: 240, type: WidthType.DXA },
    borders: noBorder,
    verticalAlign: VerticalAlign.CENTER,
    children: [
      new Paragraph({
        alignment: AlignmentType.CENTER,
        children: [
          new TextRun({
            text: "→",
            color: C.accent,
            size: 22,
            font: "Calibri",
            bold: true,
          }),
        ],
      }),
    ],
  });
}

function h1(text) {
  return new Paragraph({
    heading: HeadingLevel.HEADING_1,
    spacing: { before: 360, after: 160 },
    border: {
      bottom: { style: BorderStyle.SINGLE, size: 12, color: C.accent, space: 4 },
    },
    children: [
      new TextRun({
        text,
        bold: true,
        color: C.accent,
        size: 28,
        font: "Calibri",
      }),
    ],
  });
}

function h2(text) {
  return new Paragraph({
    heading: HeadingLevel.HEADING_2,
    spacing: { before: 280, after: 120 },
    children: [
      new TextRun({
        text,
        bold: true,
        color: C.ink,
        size: 22,
        font: "Calibri",
      }),
    ],
  });
}

function body(text) {
  return new Paragraph({
    spacing: { before: 60, after: 100 },
    children: [
      new TextRun({
        text,
        color: C.muted,
        size: 19,
        font: "Calibri",
      }),
    ],
  });
}

function quote(text) {
  return new Paragraph({
    spacing: { before: 80, after: 80 },
    indent: { left: 200 },
    border: {
      left: { style: BorderStyle.SINGLE, size: 18, color: C.accent, space: 8 },
    },
    children: [
      new TextRun({
        text: "«" + text + "»",
        italics: true,
        color: C.ink,
        size: 18,
        font: "Calibri",
      }),
    ],
  });
}

const doc = new Document({
  styles: {
    default: {
      document: {
        styles: [
          {
            id: "Normal",
            run: { font: "Calibri", size: 20, color: C.ink },
          },
        ],
      },
    },
  },
  sections: [
    {
      properties: {
        page: {
          margin: { top: 720, right: 720, bottom: 720, left: 720 },
        },
      },
      headers: {
        default: new Header({
          children: [
            new Paragraph({
              alignment: AlignmentType.RIGHT,
              children: [
                new TextRun({
                  text: "Revit AI-бот  ·  возможности для архитекторов",
                  color: C.muted,
                  size: 16,
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
              children: [
                new TextRun({
                  text: "стр. ",
                  color: C.muted,
                  size: 16,
                  font: "Calibri",
                }),
                new TextRun({
                  children: [PageNumber.CURRENT],
                  color: C.muted,
                  size: 16,
                  font: "Calibri",
                }),
              ],
            }),
          ],
        }),
      },
      children: [
        // COVER
        new Paragraph({
          spacing: { before: 600 },
          children: [
            new TextRun({
              text: "REVIT AI-БОТ",
              bold: true,
              color: C.accent,
              size: 48,
              font: "Calibri",
            }),
          ],
        }),
        new Paragraph({
          spacing: { before: 80, after: 200 },
          children: [
            new TextRun({
              text: "Что умеет делать в проекте",
              color: C.ink,
              size: 32,
              font: "Calibri",
            }),
          ],
        }),
        new Paragraph({
          spacing: { after: 320 },
          children: [
            new TextRun({
              text: "Краткий обзор для архитекторов и ГИП. Без технической настройки — только возможности и результат в модели.",
              color: C.muted,
              size: 20,
              font: "Calibri",
            }),
          ],
        }),

        // MAP
        h1("1. Карта возможностей"),
        body("Шесть блоков. Бот работает в открытом .rvt на шаблоне организации."),

        new Table({
          width: { size: 9360, type: WidthType.DXA },
          columnWidths: [3000, 180, 3000, 180, 3000],
          rows: [
            new TableRow({
              children: [
                moduleCard(
                  "МОДЕЛЬ",
                  ["Стены, полы, крыши", "Двери, окна", "Уровни, балки"],
                  C.accentSoft,
                  C.accent
                ),
                new TableCell({
                  width: { size: 180, type: WidthType.DXA },
                  borders: noBorder,
                  children: [new Paragraph({ children: [] })],
                }),
                moduleCard(
                  "ПОМЕЩЕНИЯ",
                  ["Rooms + марки", "Нумерация", "Цвета, квартирография"],
                  C.greenSoft,
                  C.green
                ),
                new TableCell({
                  width: { size: 180, type: WidthType.DXA },
                  borders: noBorder,
                  children: [new Paragraph({ children: [] })],
                }),
                moduleCard(
                  "ОСИ И РАЗМЕРЫ",
                  ["Grids по несущим", "Осевые цепочки", "Размеры комнат"],
                  C.amberSoft,
                  C.amber
                ),
              ],
            }),
          ],
        }),

        new Paragraph({ spacing: { before: 120 }, children: [] }),

        new Table({
          width: { size: 9360, type: WidthType.DXA },
          columnWidths: [3000, 180, 3000, 180, 3000],
          rows: [
            new TableRow({
              children: [
                moduleCard(
                  "ДОКУМЕНТАЦИЯ",
                  ["Спеки, экспликации", "Лист · план · автокомпоновка", "Штамп, ТЭП"],
                  C.purpleSoft,
                  C.purple
                ),
                new TableCell({
                  width: { size: 180, type: WidthType.DXA },
                  borders: noBorder,
                  children: [new Paragraph({ children: [] })],
                }),
                moduleCard(
                  "АНАЛИТИКА",
                  ["Площади, материалы", "Сверка спек", "Статистика модели"],
                  C.tealSoft,
                  C.teal
                ),
                new TableCell({
                  width: { size: 180, type: WidthType.DXA },
                  borders: noBorder,
                  children: [new Paragraph({ children: [] })],
                }),
                moduleCard(
                  "НОРМЫ",
                  ["Аудит этажа", "Заливка нарушений", "Цитаты ГОСТ/СП"],
                  C.redSoft,
                  C.red
                ),
              ],
            }),
          ],
        }),

        // FLOW
        h1("2. Типовой сценарий на плане"),
        body("От геометрии до оформления — что бот делает по цепочке:"),

        new Table({
          width: { size: 9360, type: WidthType.DXA },
          columnWidths: [2100, 240, 2100, 240, 2100, 240, 2100],
          rows: [
            new TableRow({
              children: [
                flowStep(1, "Модель", "стены · полы · проёмы", C.accentSoft, C.accent),
                arrowCell(),
                flowStep(2, "Rooms", "помещения · марки · №", C.greenSoft, C.green),
                arrowCell(),
                flowStep(3, "Аннотации", "оси · размеры · текст", C.amberSoft, C.amber),
                arrowCell(),
                flowStep(4, "Листы", "спеки · ТЭП · штамп", C.purpleSoft, C.purple),
              ],
            }),
          ],
        }),

        // MODELING
        h1("3. Моделирование"),
        pairTable([
          ["Стены, балки", "Линейные элементы по типу из проекта"],
          ["Полы, потолки, крыши", "Поверхностные элементы по контуру"],
          ["Двери, окна", "Семейства в стене-хосте"],
          ["Уровни", "Levels на заданных отметках"],
          ["Несущий каркас", "Система балок по области с шагом"],
          ["Удалить / скрыть / цвет", "Операции над элементами модели"],
        ]),

        // ROOMS
        h1("4. Помещения"),
        pairTable(
          [
            ["Помещения", "Rooms внутри замкнутого контура"],
            ["Марки помещений", "Room Tags (в т.ч. с площадью)"],
            ["Марки стен", "Wall Tags"],
            ["Нумерация", "101 / 201 по этажу или сквозная; змейка / по кругу; опционально квартиры и секции"],
            ["Цвета", "Цветовая схема вида по имени / назначению"],
            ["Подсветка площади", "Цветовые области (Filled Region)"],
            ["Выгрузка", "Имя, номер, уровень, площадь, объём, отделка"],
            ["Квартирография", "Группировка Rooms по номеру квартиры"],
          ],
          C.greenSoft
        ),

        h2("Нумерация — схема"),
        body("По умолчанию сначала preview (старый → новый номер), в модель — после подтверждения."),

        new Table({
          width: { size: 9360, type: WidthType.DXA },
          columnWidths: [3120, 3120, 3120],
          rows: [
            new TableRow({
              children: [
                cell("По этажу", {
                  bold: true,
                  fill: C.green,
                  color: C.white,
                  width: 3120,
                  align: AlignmentType.CENTER,
                }),
                cell("Сквозная", {
                  bold: true,
                  fill: C.green,
                  color: C.white,
                  width: 3120,
                  align: AlignmentType.CENTER,
                }),
                cell("Обход плана", {
                  bold: true,
                  fill: C.green,
                  color: C.white,
                  width: 3120,
                  align: AlignmentType.CENTER,
                }),
              ],
            }),
            new TableRow({
              children: [
                cell("101, 102…\n201, 202…\n(этаж × 100 + порядковый)", {
                  width: 3120,
                  align: AlignmentType.CENTER,
                  fill: C.greenSoft,
                }),
                cell("1, 2, 3…\nчерез все этажи", {
                  width: 3120,
                  align: AlignmentType.CENTER,
                  fill: C.white,
                }),
                cell("змейка по рядам\nили по / против часовой", {
                  width: 3120,
                  align: AlignmentType.CENTER,
                  fill: C.greenSoft,
                }),
              ],
            }),
          ],
        }),

        // AXES
        h1("5. Оси и размеры"),
        pairTable(
          [
            ["Координационные оси", "Grids по ЦЛ несущих стен; пузыри цифры / буквы"],
            ["Настройка осей", "Экстенты и тип марки без пересоздания"],
            ["Осевые размеры", "Внешние цепочки от габарита здания (межосевой + габарит)"],
            ["Размеры помещений", "Внутренние цепочки ширины × глубины"],
            ["Произвольные размеры", "Цепочки между элементами / точками"],
            ["Текст с выноской", "Text Notes + leader"],
          ],
          C.amberSoft
        ),
        body("Типы размеров, осей и текста — из шаблона проекта (ADSK и др.)."),

        h2("Оформление плана — схема"),
        new Table({
          width: { size: 9360, type: WidthType.DXA },
          columnWidths: [9360],
          rows: [
            new TableRow({
              children: [
                new TableCell({
                  width: { size: 9360, type: WidthType.DXA },
                  borders: thinBorder,
                  shading: { type: ShadingType.CLEAR, fill: C.amberSoft },
                  children: [
                    new Paragraph({
                      alignment: AlignmentType.CENTER,
                      spacing: { before: 140, after: 60 },
                      children: [
                        new TextRun({
                          text: "СНАРУЖИ ЗДАНИЯ",
                          bold: true,
                          color: C.amber,
                          size: 18,
                          font: "Calibri",
                        }),
                      ],
                    }),
                    new Paragraph({
                      alignment: AlignmentType.CENTER,
                      spacing: { after: 60 },
                      children: [
                        new TextRun({
                          text: "габарит корпуса  →  межосевой ярус  →  габаритный ярус  →  пузыри осей",
                          color: C.ink,
                          size: 18,
                          font: "Calibri",
                        }),
                      ],
                    }),
                    new Paragraph({
                      alignment: AlignmentType.CENTER,
                      spacing: { before: 80, after: 60 },
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
                          text: "ВНУТРИ КОМНАТ",
                          bold: true,
                          color: C.amber,
                          size: 18,
                          font: "Calibri",
                        }),
                      ],
                    }),
                    new Paragraph({
                      alignment: AlignmentType.CENTER,
                      spacing: { after: 140 },
                      children: [
                        new TextRun({
                          text: "ширина × глубина помещения  (+ при необходимости привязки проёмов)",
                          color: C.ink,
                          size: 18,
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

        // DOCS
        h1("6. Ведомости, ТЭП, листы"),

        h2("Листы и компоновка"),
        body(
          "Бот создаёт лист из шаблона проекта, ставит на него планы и спецификации, умеет автокомпоновку без ручных координат."
        ),

        new Table({
          width: { size: 9360, type: WidthType.DXA },
          columnWidths: [1700, 180, 1700, 180, 1700, 180, 1700, 180, 1740],
          rows: [
            new TableRow({
              children: [
                flowStep(1, "Лист", "Sheet + рамка", C.purpleSoft, C.purple, 1700),
                arrowCell(),
                flowStep(2, "План", "Viewport на лист", C.accentSoft, C.accent, 1700),
                arrowCell(),
                flowStep(3, "Спеки", "Schedule на лист", C.greenSoft, C.green, 1700),
                arrowCell(),
                flowStep(4, "Автокомпоновка", "ряды · поля · штамп", C.amberSoft, C.amber, 1700),
                arrowCell(),
                flowStep(5, "Штамп", "основная надпись", C.tealSoft, C.teal, 1740),
              ],
            }),
          ],
        }),

        new Paragraph({ spacing: { before: 200 }, children: [] }),

        pairTable(
          [
            [
              "Создать лист",
              "Sheet с title block (рамкой) из проекта — семейство основной надписи организации",
            ],
            [
              "Разместить план",
              "Viewport: план этажа / вид на лист в заданной позиции (мм)",
            ],
            [
              "Разместить спецификацию",
              "ScheduleSheetInstance: спека / экспликация / ведомость на лист",
            ],
            [
              "Автокомпоновка",
              "Сама меряет габариты видов и спек, пакует рядами в рабочей зоне; обходит поля ГОСТ и зону штампа; при необходимости подгоняет масштаб плана (спеки не масштабирует)",
            ],
            [
              "Основная надпись",
              "Заполнение штампа (СПДС/ГОСТ) полями проекта — без новых семейств рамок",
            ],
          ],
          C.purpleSoft
        ),

        new Paragraph({ spacing: { before: 160 }, children: [] }),

        new Table({
          width: { size: 9360, type: WidthType.DXA },
          columnWidths: [9360],
          rows: [
            new TableRow({
              children: [
                new TableCell({
                  width: { size: 9360, type: WidthType.DXA },
                  borders: thinBorder,
                  shading: { type: ShadingType.CLEAR, fill: C.purpleSoft },
                  children: [
                    new Paragraph({
                      alignment: AlignmentType.CENTER,
                      spacing: { before: 120, after: 40 },
                      children: [
                        new TextRun({
                          text: "АВТОКОМПОНОВКА ЛИСТА — что учитывает",
                          bold: true,
                          color: C.purple,
                          size: 18,
                          font: "Calibri",
                        }),
                      ],
                    }),
                    new Paragraph({
                      alignment: AlignmentType.CENTER,
                      spacing: { after: 40 },
                      children: [
                        new TextRun({
                          text: "рабочая зона листа  ·  поля (слева 20 мм и др.)  ·  зона штампа снизу/справа  ·  зазор между элементами",
                          color: C.ink,
                          size: 17,
                          font: "Calibri",
                        }),
                      ],
                    }),
                    new Paragraph({
                      alignment: AlignmentType.CENTER,
                      spacing: { after: 120 },
                      children: [
                        new TextRun({
                          text: "уже стоящие виды/спеки как препятствия  ·  dependent view, если план уже на другом листе  ·  oversized спека → предупреждение (разбить вручную)",
                          color: C.muted,
                          size: 16,
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

        h2("Спецификации и данные"),
        pairTable(
          [
            ["Спеки дверей / окон", "Schedule (блоки без откосов)"],
            ["Экспликация полов", "Только отделка (полы)*, площади м²"],
            [
              "Лист экспликации полов",
              "Спеки по группам этажей + создание листа + раскладка (в т.ч. перелив на ЭП-02…)",
            ],
            ["Ведомость отделки", "Цепочка спек по шаблону ADSK"],
            ["Витражи", "Данные / спека curtain wall systems"],
            ["ТЭП", "Выгрузка показателей; таблица ТЭП на листе"],
            ["Сверка спеки", "Отчёт расхождений с моделью"],
            ["Материалы", "Площадь, объём, число элементов"],
          ],
          C.purpleSoft
        ),

        // NORMS
        h1("7. Нормоконтроль"),
        body("Библиотека ГОСТ / СП / СН РК. Числа не выдумываются — только из документов с цитатой."),

        h2("Схема проверки этажа"),
        new Table({
          width: { size: 9360, type: WidthType.DXA },
          columnWidths: [2200, 200, 2200, 200, 2200, 200, 2160],
          rows: [
            new TableRow({
              children: [
                flowStep(1, "Нормы", "тема / библиотека", C.redSoft, C.red),
                arrowCell(),
                flowStep(2, "Аудит", "сводный отчёт", C.amberSoft, C.amber),
                arrowCell(),
                flowStep(3, "Заливка", "Filled Region", C.accentSoft, C.accent),
                arrowCell(),
                flowStep(4, "Подписи", "текст + выноска", C.greenSoft, C.green),
              ],
            }),
          ],
        }),

        new Paragraph({ spacing: { before: 200 }, children: [] }),

        pairTable(
          [
            ["Спросить норму", "Документ, пункт, цитата"],
            [
              "Проверить этаж",
              "Коридоры, глубины, лоджии/балконы/простенки, ПД, двери, площади/высоты, проёмы, лестницы/пандусы, тамбуры, МГН",
            ],
            ["Показать нарушения", "Заливка помещений + цвет дверей"],
            ["Подписать нарушения", "Текст с выноской к элементу"],
            ["Записать статус", "Параметры / марки в модели"],
          ],
          C.redSoft
        ),
        body("Нет правила в библиотеке → проверка «пропущена», а не «всё ОК»."),

        // READ
        h1("8. Чтение модели"),
        new Table({
          width: { size: 9360, type: WidthType.DXA },
          columnWidths: [4680, 4680],
          rows: [
            new TableRow({
              children: [
                cell("•  Активный вид (тип, имя, масштаб, уровень)", {
                  width: 4680,
                  fill: C.soft,
                  borders: thinBorder,
                }),
                cell("•  Элементы на виде и выделение", {
                  width: 4680,
                  fill: C.white,
                  borders: thinBorder,
                }),
              ],
            }),
            new TableRow({
              children: [
                cell("•  Параметры любого элемента", {
                  width: 4680,
                  fill: C.white,
                  borders: thinBorder,
                }),
                cell("•  Типы семейств в проекте", {
                  width: 4680,
                  fill: C.soft,
                  borders: thinBorder,
                }),
              ],
            }),
            new TableRow({
              children: [
                cell("•  Стили: размеры, оси, текст, штампы", {
                  width: 4680,
                  fill: C.soft,
                  borders: thinBorder,
                }),
                cell("•  Статистика модели, геометрия для проверок", {
                  width: 4680,
                  fill: C.white,
                  borders: thinBorder,
                }),
              ],
            }),
          ],
        }),

        // PHRASES
        h1("9. Примеры формулировок"),
        body("Пишете обычным языком — бот сам выбирает команды:"),
        quote("На этом этаже оси по несущим, размеры снаружи и внутри комнат"),
        quote("Поставь Rooms и марки с площадью"),
        quote("Пронумеруй помещения: 101, 102… по этажам, змейкой; сначала покажи план"),
        quote("Сделай экспликацию полов и лист А2"),
        quote("Создай лист, поставь план 1 этажа и спеки, разложи автокомпоновкой"),
        quote("Заполни штамп на листе"),
        quote("Спеки дверей и окон, сверь с моделью"),
        quote("Выгрузи ТЭП и квартирографию"),
        quote("Проверь этаж по нормам, покажи нарушения заливкой и подпиши"),

        new Paragraph({ spacing: { before: 400 }, children: [] }),
        new Paragraph({
          alignment: AlignmentType.CENTER,
          children: [
            new TextRun({
              text: "— конец документа —",
              color: C.muted,
              size: 16,
              font: "Calibri",
            }),
          ],
        }),
      ],
    },
  ],
});

const out = path.join(__dirname, "Revit-AI-bot-vozmozhnosti.docx");
const buffer = await Packer.toBuffer(doc);
fs.writeFileSync(out, buffer);
console.log("Wrote", out);
