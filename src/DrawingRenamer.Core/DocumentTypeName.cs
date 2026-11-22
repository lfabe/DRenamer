using System.Text.RegularExpressions;

namespace DrawingRenamer;

internal static class DocumentTypeName
{
    private const string SchemeKind =
        @"(?:(?:электро|гидро|пневмо|газо|кинемато|вакуумно|оптико|энерго){0,9}(?:электрическая|гидравлическая|пневматическая|газовая|кинематическая|вакуумная|оптическая|энергетическая)|деления|комбинированная)";
    private const string SchemeType =
        @"(?:структурная|функциональная|принципиальная(?:\s*\(полная\))?|полная|соединений(?:\s*\(монтажная\))?|монтажная|подключения|общая|расположения|объедин[её]нная)";
    private const string SchemeTypes = $@"{SchemeType}(?:(?:\s*,\s*|\s+и\s+){SchemeType})*";
    private const string Scheme =
        $@"схема\s+(?:{SchemeKind}(?:\s+{SchemeTypes})?|{SchemeTypes})|{SchemeKind}\s+(?:{SchemeTypes}\s+)?схема|{SchemeType}\s+(?:{SchemeKind}\s+)?схема";

    private const string StrongSeparators = "._,:;-‐‑‒–—\r\n";
    private static readonly char[] Separators = (" \t\u00A0\u202F" + StrongSeparators).ToCharArray();
    private static readonly Regex Types = BuildPattern();

    internal static string? ForFileName(string title)
    {
        foreach (Match match in Types.Matches(title))
        {
            var before = title[..match.Index];
            var after = title[(match.Index + match.Length)..];
            var left = before.TrimEnd();
            var right = after.TrimStart();
            var wrapped = left.EndsWith('(') && right.StartsWith(')') ||
                          left.EndsWith('«') && right.StartsWith('»');
            if (wrapped)
            {
                before = left[..^1];
                after = right[1..];
            }

            left = before.TrimEnd(Separators);
            right = after.TrimStart(Separators);
            var leftSeparator = before.AsSpan(left.Length);
            var rightSeparator = after.AsSpan(0, after.Length - right.Length);
            var separateEntry = (left.Length == 0 || leftSeparator.ContainsAny(StrongSeparators)) &&
                rightSeparator.ContainsAny(StrongSeparators);
            var startsWithType = left.Length == 0 && match.Value.Any(char.IsWhiteSpace);
            if (right.Length != 0 && !wrapped && !separateEntry && !startsWithType && !match.Groups["scheme"].Success) continue;

            if (left.Length == 0) return right.Length == 0 ? null : right;
            if (right.Length == 0) return left;
            var separator = leftSeparator.Contains('.') || rightSeparator.Contains('.') ? ". " : " ";
            return left + separator + right;
        }

        return title;
    }

    private static Regex BuildPattern()
    {
        string[] names =
        [
            // ГОСТ Р 2.102-2023, раздел 4, включая листы по п. 4.5.
            "электронная модель детали", "чертеж детали", "электронная модель сборочной единицы",
            "электронная модель специализированная", "сборочный чертеж", "чертеж общего вида",
            "теоретический чертеж", "габаритный чертеж", "электромонтажный чертеж", "монтажный чертеж",
            "упаковочный чертеж", "электронная конструктивная структура изделия", "спецификация",
            "ведомость спецификаций", "ведомость ссылочных документов", "ведомость покупных изделий",
            "ведомость разрешения применения покупных изделий", "ведомость держателей подлинников",
            "ведомость технического предложения", "ведомость эскизного проекта", "ведомость технического проекта",
            "пояснительная записка", "ведомость электронной конструкторской документации",
            "технические условия", "программа и методика испытаний", "таблица", "расчет", "инструкция",
            "прочие документы", "эксплуатационные документы", "ремонтные документы", "лист утверждения", "удостоверяющий лист",

            // ГОСТ Р 2.601-2019, таблицы 1 и 2.
            "руководство по эксплуатации", "инструкция по монтажу, пуску, регулированию и обкатке изделия",
            "формуляр", "паспорт", "этикетка", "каталог изделия", "нормы расхода запасных частей",
            "нормы расхода материалов", "ведомость комплекта запасных частей, инструмента и принадлежностей",
            "ведомость зип", "учебно-технические плакаты", "инструкции эксплуатационные специальные",
            "ведомость эксплуатационных документов",

            // ГОСТ 2.602-2013, таблицы 2 и 3, и ремонтные виды по ГОСТ 2.604-2000.
            "руководство по ремонту", "общее руководство по ремонту",
            "технические условия на ремонт", "общие технические условия на ремонт",
            "чертежи ремонтные", "ремонтный чертеж", "ремонтный сборочный чертеж", "ремонтный монтажный чертеж",
            "ремонтная спецификация", "ремонтная схема", "ремонтная ведомость спецификаций", "ремонтная инструкция",
            "нормы расхода запасных частей на ремонт", "нормы расхода материалов на ремонт", "ведомость зип на ремонт",
            "документация на средства оснащения ремонта", "техническая документация на средства оснащения ремонта",
            "ведомость документов для ремонта",

            // ГОСТ 2.701-2008, п. 4.5. Виды и типы схем по таблицам 1 и 2 заданы в Scheme.
            "таблица соединений"
        ];
        var entries = names.OrderByDescending(name => name.Length).Select(name =>
            Regex.Escape(name).Replace(@"\ ", @"\s+").Replace(@",\s+", @"(?:\s*,\s*|\s+)")
                .Replace("е", "[её]").Replace("-", "[-‐‑‒–—]"));
        return new($@"(?<![\p{{L}}\p{{N}}])(?:{string.Join('|', entries)}|(?<scheme>{Scheme})|схема)(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
