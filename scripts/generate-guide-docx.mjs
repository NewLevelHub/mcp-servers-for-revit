/**
 * Builds docs/AI-assistent-Revit-instrukciya.docx - the guide handed to architects.
 * Mirrors the published web version; screenshots come from docs/assets.
 *
 * `docx` is not a repo dependency and there is no root package.json. Node resolves
 * imports from the script's own folder upwards, so it has to sit in the repo root -
 * installing it in some other working directory does not help:
 *   npm install --no-save docx
 *   node scripts/generate-guide-docx.mjs
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  AlignmentType, BorderStyle, Document, Footer, HeadingLevel, ImageRun, Packer,
  PageNumber, Paragraph, ShadingType, Table, TableCell, TableRow, TextRun,
  VerticalAlign, WidthType, convertInchesToTwip,
} from "docx";

const __root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const outPath = process.argv[2] || path.join(__root, "docs", "AI-assistent-Revit-instrukciya.docx");
const imgDir = path.resolve(process.argv[3] || path.join(__root, "docs", "assets"));

const NAVY = "1A2744";
const STEEL = "2F5D8A";
const INK = "16202F";
const MUTED = "5B6B7C";
const LINE = "D9E2EC";
const HEADBG = "F0F4F8";
const WARNBG = "FFF6E5";
const OK = "1F6B4A";
const WARN = "8A6A00";

const FONT = "Segoe UI";
const SERIF = "Georgia";

/** Paragraph helpers -------------------------------------------------- */

const h1 = (text) => new Paragraph({
  spacing: { before: 0, after: 160 },
  children: [new TextRun({ text, font: SERIF, size: 52, bold: true, color: NAVY })],
});

const h2 = (text) => new Paragraph({
  spacing: { before: 420, after: 100 },
  children: [new TextRun({ text, font: SERIF, size: 30, bold: true, color: NAVY })],
});

const h3 = (text) => new Paragraph({
  spacing: { before: 240, after: 80 },
  children: [new TextRun({ text, font: FONT, size: 22, bold: true, color: NAVY })],
});

const eyebrow = (text) => new Paragraph({
  spacing: { after: 100 },
  children: [new TextRun({ text: text.toUpperCase(), font: FONT, size: 15, bold: true, color: STEEL, characterSpacing: 40 })],
});

const p = (text, opts = {}) => new Paragraph({
  spacing: { after: opts.after ?? 140 },
  children: [new TextRun({ text, font: FONT, size: opts.size ?? 20, color: opts.color ?? INK, italics: opts.italics })],
});

/** Runs with **bold** segments so key phrases carry through to Word. */
const rich = (text, opts = {}) => new Paragraph({
  spacing: { after: opts.after ?? 140 },
  indent: opts.indent,
  children: text.split(/(\*\*[^*]+\*\*)/).filter(Boolean).map((chunk) => {
    const bold = chunk.startsWith("**") && chunk.endsWith("**");
    return new TextRun({
      text: bold ? chunk.slice(2, -2) : chunk,
      font: FONT, size: opts.size ?? 20,
      color: opts.color ?? INK, bold,
    });
  }),
});

const sub = (text) => p(text, { color: MUTED, size: 19, after: 200 });

const bullet = (text) => new Paragraph({
  bullet: { level: 0 },
  spacing: { after: 90 },
  children: text.split(/(\*\*[^*]+\*\*)/).filter(Boolean).map((chunk) => {
    const bold = chunk.startsWith("**") && chunk.endsWith("**");
    return new TextRun({ text: bold ? chunk.slice(2, -2) : chunk, font: FONT, size: 20, color: INK, bold });
  }),
});

const numbered = (n, title, body) => [
  new Paragraph({
    spacing: { before: 160, after: 40 },
    children: [new TextRun({ text: `${n}. ${title}`, font: FONT, size: 20, bold: true, color: NAVY })],
  }),
  new Paragraph({
    spacing: { after: 100 },
    indent: { left: convertInchesToTwip(0.25) },
    children: [new TextRun({ text: body, font: FONT, size: 20, color: INK })],
  }),
];

const note = (text) => new Table({
  width: { size: 100, type: WidthType.PERCENTAGE },
  borders: {
    top: { style: BorderStyle.SINGLE, size: 2, color: WARNBG },
    bottom: { style: BorderStyle.SINGLE, size: 2, color: WARNBG },
    right: { style: BorderStyle.SINGLE, size: 2, color: WARNBG },
    left: { style: BorderStyle.SINGLE, size: 18, color: "E0A800" },
    insideHorizontal: { style: BorderStyle.NONE },
    insideVertical: { style: BorderStyle.NONE },
  },
  rows: [new TableRow({
    children: [new TableCell({
      shading: { type: ShadingType.CLEAR, fill: WARNBG },
      margins: { top: 140, bottom: 140, left: 200, right: 200 },
      children: text.map((t) => rich(t, { color: "5A4300", after: 60 })),
    })],
  })],
});

const cell = (children, opts = {}) => new TableCell({
  verticalAlign: VerticalAlign.TOP,
  shading: opts.fill ? { type: ShadingType.CLEAR, fill: opts.fill } : undefined,
  margins: { top: 90, bottom: 90, left: 120, right: 120 },
  width: opts.width ? { size: opts.width, type: WidthType.PERCENTAGE } : undefined,
  children,
});

const table = (head, rows, widths) => new Table({
  width: { size: 100, type: WidthType.PERCENTAGE },
  borders: {
    top: { style: BorderStyle.SINGLE, size: 4, color: LINE },
    bottom: { style: BorderStyle.SINGLE, size: 4, color: LINE },
    left: { style: BorderStyle.NONE }, right: { style: BorderStyle.NONE },
    insideHorizontal: { style: BorderStyle.SINGLE, size: 2, color: LINE },
    insideVertical: { style: BorderStyle.NONE },
  },
  rows: [
    new TableRow({
      tableHeader: true,
      children: head.map((t, i) => cell(
        [new Paragraph({ children: [new TextRun({ text: t.toUpperCase(), font: FONT, size: 15, bold: true, color: MUTED, characterSpacing: 30 })] })],
        { fill: HEADBG, width: widths?.[i] },
      )),
    }),
    ...rows.map((r) => new TableRow({
      children: r.map((t, i) => cell(
        [rich(t, { size: 19, after: 0, color: i === 0 ? NAVY : INK })],
        { width: widths?.[i] },
      )),
    })),
  ],
});

const image = (file, widthPx) => {
  const data = fs.readFileSync(path.join(imgDir, file));
  const meta = { png: { type: "png" }, jpg: { type: "jpg" } }[path.extname(file).slice(1).replace("jpeg", "jpg")];
  const dims = file.endsWith(".jpg") ? { w: 1280, h: 771 } : { w: 385, h: 400 };
  const scale = widthPx / dims.w;
  return new Paragraph({
    spacing: { before: 160, after: 80 },
    children: [new ImageRun({ data, type: meta.type, transformation: { width: widthPx, height: Math.round(dims.h * scale) } })],
  });
};

const caption = (text) => p(text, { color: MUTED, size: 17, after: 200 });

/** Document ------------------------------------------------------------ */

const doc = new Document({
  creator: "mcp-servers-for-revit",
  title: "AI-ассистент в Revit",
  description: "Инструкция для архитектора",
  sections: [{
    properties: {
      page: { margin: { top: convertInchesToTwip(0.9), bottom: convertInchesToTwip(0.9), left: convertInchesToTwip(1), right: convertInchesToTwip(1) } },
    },
    footers: {
      default: new Footer({
        children: [new Paragraph({
          alignment: AlignmentType.RIGHT,
          children: [new TextRun({ children: ["AI-ассистент в Revit · ", PageNumber.CURRENT], font: FONT, size: 16, color: MUTED })],
        })],
      }),
    },
    children: [
      eyebrow("Инструкция для архитектора"),
      h1("AI-ассистент в Revit"),
      p("Чат внутри Revit, который выполняет рутину прямо в открытой модели: ставит помещения и марки, оси и размеры, собирает спецификации и листы, проверяет планировку по нормам. Разговаривать с ним нужно обычными словами — как с коллегой, а не командами.", { size: 21, color: MUTED, after: 160 }),
      p("Revit 2023 · лента → AI-ассистент · обновлено 17.08.2026", { size: 17, color: MUTED, after: 320 }),

      h2("Где он живёт"),
      sub("Панель открывается кнопкой на ленте и остаётся справа, пока вы работаете."),
      p("В шапке панели всегда видно, с чем ассистент имеет дело прямо сейчас: документ, активный вид, масштаб и уровень. Если там написано не то, что вы ожидаете — переключите вид в Revit, панель подхватит его сама."),
      note(["**Cursor открывать больше не нужно.** Раньше ассистент работал из отдельного приложения, и связь между ним и Revit периодически рвалась. Теперь всё живёт внутри Revit: запустили Revit — ассистент готов, закрыли — выключился."]),
      image("panel-overview.jpg", 620),
      caption("Панель занимает правый край окна. Модель остаётся видимой — результат появляется в чертеже сразу, без переключений."),

      h2("Как формулировать запрос"),
      sub("Это влияет на результат сильнее всего остального."),
      p("Ассистент видит активный вид и то, что выделено, поэтому «проставь размеры здесь» он поймёт. Три вещи, которые стоит держать в голове:"),
      bullet("**Называйте задачу, а не инструмент.** «Собери спецификацию дверей» работает, «запусти schedule» — нет. Ассистент сам выбирает, чем это сделать."),
      bullet("**Один запрос — одна задача.** Цепочка из пяти дел за раз ошибается заметно чаще, чем те же пять запросов подряд."),
      bullet("**Открывайте нужный вид заранее.** Работа идёт по активному виду. Если открыт разрез, а вы просите марки помещений — результата не будет."),

      h3("Примеры, которые работают"),
      bullet("«На этом этаже поставь оси по несущим стенам»"),
      bullet("«Поставь помещения и марки с площадью»"),
      bullet("«Пронумеруй помещения 101, 102… змейкой, сначала покажи план»"),
      bullet("«Собери спецификации дверей и окон, сверь с моделью»"),
      bullet("«Создай лист, поставь план 1 этажа и спеки, разложи автокомпоновкой»"),
      bullet("«Проверь этаж по нормам, покажи нарушения заливкой и подпиши»"),
      bullet("«Выгрузи ТЭП и квартирографию»"),

      h2("Что он умеет"),
      sub("Всё это происходит в открытом проекте, на типах и марках вашего шаблона."),
      table(["Блок", "Что получится в модели"], [
        ["Модель", "Стены, полы, крыши, потолки, двери, окна, уровни, балки, лестницы, ограждения, проёмы и шахты"],
        ["Помещения", "Rooms по замкнутому контуру, марки с площадью, нумерация по этажам или сквозная, цветовые схемы, квартирография"],
        ["Оси и размеры", "Координационные оси по несущим, внешние размерные ярусы от габарита, внутренние цепочки по комнатам, текст с выноской"],
        ["Листы", "Лист из шаблона, планы и спецификации на нём, автокомпоновка с полями и штампом, заполнение основной надписи"],
        ["Спецификации", "Двери, окна, витражи, экспликация полов, ведомость отделки, ТЭП, сверка спецификации с моделью"],
        ["Нормы", "Аудит этажа по ГОСТ / СП / СН РК с цитатой пункта, заливка помещений-нарушений, подписи к ним"],
        ["Чтение модели", "Что на активном виде, параметры любого элемента, доступные типы семейств, площади и объёмы материалов, статистика проекта"],
      ], [22, 78]),
      new Paragraph({ spacing: { after: 160 }, children: [] }),
      note(["**Числа он проверяет по модели, а нормы — по библиотеке документов.** Если нужного правила в библиотеке нет, проверка помечается как пропущенная, а не как «всё в порядке». Выдуманных пунктов СП быть не должно — если увидели такой, это повод для жалобы с тегом «выдумал нормы»."]),

      h2("Что поручить в первую очередь"),
      sub("Задачи, на которых он экономит больше всего времени. Слева — что сказать своими словами."),
      table(["Скажите", "Что получится в модели"], [
        ["«Поставь помещения и марки с площадью»", "Rooms по всем замкнутым контурам этажа и марки к ним. На большом плане это десятки элементов за один запрос"],
        ["«Пронумеруй помещения 101, 102… змейкой, сначала покажи план»", "Сначала предпросмотр «старый номер → новый», в модель — только после вашего подтверждения"],
        ["«Поставь оси по несущим стенам»", "Grids по центральным линиям несущих, пузыри цифрами снизу и буквами слева"],
        ["«Собери спецификации дверей и окон и сверь с моделью»", "Schedules по шаблону проекта плюс отчёт о расхождениях между спекой и тем, что реально стоит в модели"],
        ["«Сделай экспликацию полов и лист А2»", "Спецификации по группам этажей, лист из вашего шаблона и раскладка на нём"],
        ["«Создай лист, поставь план 1 этажа и спеки, разложи автокомпоновкой»", "Sheet с рамкой, viewport плана и спеки, расставленные рядами с учётом полей и зоны штампа — без ручных координат"],
        ["«Заполни штамп на листе»", "Основная надпись по СПДС/ГОСТ заполняется полями проекта"],
        ["«Проверь этаж по нормам, покажи нарушения заливкой и подпиши»", "Аудит по коридорам, глубинам, лоджиям, дверям, площадям и высотам, МГН и путям эвакуации. Нарушения — заливкой, с текстом и выноской, каждое с цитатой пункта"],
        ["«Выгрузи ТЭП и квартирографию»", "Показатели и группировка помещений по квартирам — таблицей, готовой к выносу на лист"],
        ["«Что на этом виде» / «покажи параметры выделенного»", "Точные числа по модели, а не на глаз: состав вида, параметры элемента, доступные типы семейств, площади и объёмы материалов"],
      ], [38, 62]),

      h2("Что изменилось по вашим замечаниям"),
      sub("Вы присылали список по итогам прошлого тестирования. Вот что с каждым пунктом стало — включая то, что ещё не закрыто."),
      table(["Ваше замечание", "Статус", "Что сделано"], [
        ["Периодически теряется связь между Cursor и Revit", "СНЯТО", "Отдельного приложения больше нет — ассистент живёт внутри Revit и запускается вместе с ним. Если движок всё-таки перестал отвечать, он теперь так и пишет и называет лекарство: Настройки → Ассистент → «Перезапустить движок». Revit закрывать не нужно"],
        ["Размерные линии создаются с ошибками, неверные привязки", "УЛУЧШЕНО", "Размеры теперь цепляются к граням элементов, а не к приблизительным точкам; добавлен отдельный ярус проёмов и простенков; после простановки ассистент проверяет, что получилось; внешний ярус масштабируется под вид. Точность просим проверять и жаловаться тегом «не те привязки»"],
        ["Стены по DWG выстраиваются с отклонениями от исходной геометрии", "УЛУЧШЕНО", "Трассировка по подложке переписана и дважды упрочнена, добавлено распознавание дверей, окон и колонн. Это самое сложное место во всей системе — именно сюда просим направить тестирование, тег «построил неточно»"],
        ["Автоматическое создание узлов не тестировалось", "НУЖЕН ЭТАЛОН", "Генератор появился: он читает реальный пирог стены или пола — слои, толщины, материалы — и собирает чертёжный вид, не угадывая конструктив. Но чтобы узел выглядел как в вашем альбоме, нужен один эталонный узел от вас с вашими семействами и штриховками"],
        ["Требуется доработка — но сообщать о проблемах было некуда", "СДЕЛАНО", "Кнопка «палец вниз» под каждым ответом. Жалоба уходит разработчику вместе со скриншотом, вашим запросом, ответом ассистента и списком того, что он успел сделать в модели"],
      ], [26, 14, 60]),
      new Paragraph({ spacing: { after: 160 }, children: [] }),
      note([
        "**Два тега в форме жалобы появились специально под ваши замечания** — «построил неточно» и «не те привязки». Раньше такую претензию было некуда положить, и она терялась среди остальных.",
        "По двум пунктам работа не закончена, и мы это не прячем. Ваши жалобы со скриншотами — самый короткий путь к тому, чтобы закончить её правильно, а не наугад.",
      ]),

      h2("Сколько ждать ответа"),
      p("Первое слово появляется через 10–20 секунд, дальше текст печатается на глазах. Это нормальная скорость: перед ответом ассистент читает контекст модели. Задачи с созданием элементов идут дольше — от полуминуты до нескольких минут, выполненные шаги видно в панели по ходу дела."),
      p("Ждать не обязательно: кнопка «Стоп» прерывает работу в любой момент. Если диалог стал длинным и ассистент начал путаться в контексте — нажмите «+ Новый», это очищает историю и обычно помогает."),

      h2("Кнопки под ответом"),
      sub("Появляются под каждым ответом ассистента."),
      image("panel-top.png", 300),
      caption("Под ответом — ряд кнопок. Строкой ниже мелким шрифтом видно, какая модель отвечала."),
      table(["Кнопка", "Что делает"], [
        ["Палец вверх", "Ответ хороший. Одно нажатие, ничего заполнять не нужно"],
        ["Палец вниз", "Что-то не так — открывает форму жалобы"],
        ["Копировать", "Копирует текст ответа в буфер обмена"],
        ["Повторить", "Повторяет тот же запрос заново"],
        ["Изменить", "Возвращает ваш запрос в поле ввода, чтобы переформулировать"],
      ], [22, 78]),
      new Paragraph({ spacing: { after: 160 }, children: [] }),
      note(["**Оценка — не вежливость, а рабочий инструмент.** По ней разработчик видит, что чинить в первую очередь. Ответ без оценки для него не существует."]),

      h2("Как пожаловаться"),
      sub("Полминуты вашего времени — и проблема попадает разработчику вместе с чертежом."),
      ...numbered(1, "Нажмите «палец вниз» под плохим ответом", "Откроется жёлтая форма."),
      ...numbered(2, "Выберите тег", "Одним нажатием. Тег важнее комментария — по нему жалобы сортируются."),
      ...numbered(3, "Напишите пару слов", "Своими словами, коротко: «размеры встали не по осям». Необязательно, но сильно помогает."),
      ...numbered(4, "Нажмите «Приложить скрин»", "Снимок окна Revit попадёт в отчёт вместе с жалобой. Появится маленькая картинка — значит снято. Заранее откройте тот вид, где видно проблему."),
      ...numbered(5, "Нажмите «Отправить»", "Под ответом появится зелёное «Записано». Больше ничего делать не нужно — жалоба уедет разработчику сама."),

      h3("Какой тег выбрать"),
      table(["Тег", "Когда"], [
        ["не понял запрос", "Сделал не то, о чём вы просили"],
        ["не тот инструмент", "Задачу понял, но взялся за неё не с той стороны"],
        ["построил неточно", "Элементы встали с отклонениями — не по подложке, не по осям"],
        ["не те привязки", "Размер, марка или выноска привязаны не к тем точкам"],
        ["сломал модель", "После действия в проекте что-то испортилось"],
        ["выдумал нормы", "Сослался на пункт, которого нет, или назвал не то число"],
        ["не довёл до конца", "Начал и бросил на середине"],
        ["слишком долго", "Ждать пришлось дольше, чем сделать руками"],
        ["ошибка/упал", "Прервался с ошибкой, ничего не сделал"],
      ], [26, 74]),
      new Paragraph({ spacing: { after: 160 }, children: [] }),
      note(["**Отправить можно и без текста** — хватит одного тега или одного скриншота. Не откладывайте жалобу «на потом»: через час вы уже не вспомните, на каком виде и что именно пошло не так, а скриншот помнит."]),

      h2("Если что-то пошло не так"),
      table(["Что видите", "Что делать"], [
        ["«Движок ассистента потерял связь с Cursor»", "Настройки → Ассистент → «Перезапустить движок», затем повторите запрос. Revit закрывать не нужно"],
        ["Ответ не приходит очень долго", "Нажмите «Стоп» и повторите запрос. Если повторяется — перезапустите движок"],
        ["Путается, ссылается на старое", "«+ Новый» — очищает историю диалога"],
        ["Сделал не то в модели", "Обычный Ctrl+Z в Revit. Затем «палец вниз» со скриншотом"],
      ], [34, 66]),
      new Paragraph({ spacing: { after: 160 }, children: [] }),
      note(["**Опасные действия ассистент спрашивает отдельно.** Удаление, изменение больше двадцати элементов разом и запуск кода требуют подтверждения — карточка появится прямо в панели. Если не уверены, нажимайте «Отмена»: без вашего согласия такое действие не выполнится."]),

      new Paragraph({ spacing: { before: 420, after: 120 }, border: { top: { style: BorderStyle.SINGLE, size: 12, color: NAVY, space: 8 } }, children: [] }),
      p("Ассистент сейчас в опытной эксплуатации. Каждая жалоба со скриншотом попадает разработчику вместе с вашим запросом, ответом ассистента и списком выполненных действий — это самый быстрый способ повлиять на то, что починят следующим.", { color: MUTED, size: 18 }),
    ],
  }],
});

const buf = await Packer.toBuffer(doc);
fs.mkdirSync(path.dirname(outPath), { recursive: true });
fs.writeFileSync(outPath, buf);
console.log(`готово: ${outPath} (${Math.round(buf.length / 1024)} КБ)`);
