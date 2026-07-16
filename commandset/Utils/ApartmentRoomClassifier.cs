namespace RevitMCPCommandSet.Utils
{
    public enum ApartmentRoomCategory
    {
        Living,
        Auxiliary,
        Summer
    }

    /// <summary>
    /// Classifies apartment rooms by name into living / auxiliary / summer categories
    /// and assigns reduction coefficients per СП РК 3.02-101-2012*, приложение А, п. А.8:
    /// балконы и террасы — 0,3; лоджии — 0,5; веранды — 0,8; совмещённые лоджии, балконы — 0,4.
    /// </summary>
    public static class ApartmentRoomClassifier
    {
        public const double BalconyTerraceCoefficient = 0.3;
        public const double LoggiaCoefficient = 0.5;
        public const double VerandaCoefficient = 0.8;
        public const double CombinedCoefficient = 0.4;

        public const string NormCode = "СП РК 3.02-101-2012*";
        public const string NormClause = "Приложение А, пункт А.8";

        public const string NormQuote =
            "Общая площадь жилища (квартиры) определяется как сумма полезной площади жилища, " +
            "включающая жилые, нежилые помещения и площади балконов (лоджий, веранд, террас), " +
            "рассчитываемых с применением следующих понижающих коэффициентов: " +
            "для балконов и террас - 0,3, лоджий - 0,5, веранд - 0,8. " +
            "Для совмещенных лоджий, балконов применяется понижающий коэффициент 0,4.";

        // А.8: спальни, гостиные, детские, домашний кабинет, библиотека, столовая, игровые.
        private static readonly string[] LivingTokens =
        {
            "спальн", "гостин", "детск", "кабинет", "библиотек", "столов", "игров", "жилая комната"
        };

        public static ApartmentRoomCategory Classify(string roomName, out string summerKind, out double coefficient)
        {
            summerKind = null;
            coefficient = 1.0;

            var name = (roomName ?? string.Empty).ToLowerInvariant();

            bool loggia = name.Contains("лодж");
            bool balcony = name.Contains("балкон");
            bool terrace = name.Contains("террас");
            bool veranda = name.Contains("веранд");

            if (loggia || balcony || terrace || veranda)
            {
                if (name.Contains("совмещ") && (loggia || balcony))
                {
                    summerKind = "combined";
                    coefficient = CombinedCoefficient;
                }
                else if (loggia)
                {
                    summerKind = "loggia";
                    coefficient = LoggiaCoefficient;
                }
                else if (veranda)
                {
                    summerKind = "veranda";
                    coefficient = VerandaCoefficient;
                }
                else
                {
                    summerKind = balcony ? "balcony" : "terrace";
                    coefficient = BalconyTerraceCoefficient;
                }

                return ApartmentRoomCategory.Summer;
            }

            // «нежилое …» — явно подсобное, до проверки жилых токенов
            // (иначе «нежилая» совпала бы с токеном «жилая…»).
            if (name.Contains("нежил"))
                return ApartmentRoomCategory.Auxiliary;

            // А.8 относит кухни (включая кухню-нишу и кухню-столовую) к нежилым;
            // кухня-гостиная в практике учитывается жилой — токен «гостин» побеждает.
            if (name.Contains("кухн") && !name.Contains("гостин"))
                return ApartmentRoomCategory.Auxiliary;

            foreach (var token in LivingTokens)
            {
                if (name.Contains(token))
                    return ApartmentRoomCategory.Living;
            }

            return ApartmentRoomCategory.Auxiliary;
        }

        /// <summary>Тип квартиры по числу жилых комнат: Студия, 1К, 2К…</summary>
        public static string GetApartmentType(int livingRoomCount)
        {
            return livingRoomCount <= 0 ? "Студия" : $"{livingRoomCount}К";
        }
    }
}
