using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Mock Data Generator - the word lists the fake values are drawn from. Plain
    /// arrays so the generator compiles without WPF. Names and places are common
    /// and made up; e-mail, web and IP values use the addresses reserved for
    /// examples (example.com, 192.0.2.x ...), so test data can't reach a real person.
    /// </summary>
    public static partial class MockDataManager
    {
        internal sealed class Pool
        {
            public required string[] FirstNames, LastNames, Companies, CompanySuffixes, JobTitles,
                StreetNames, StreetKinds, Cities, Countries;
            public required string PhoneFormat;      // '#' = a digit
            public required string PostalFormat;
            public required bool NumberBeforeStreet;
        }

        internal static readonly string[] EmailDomains = { "example.com", "example.org", "example.net" };

        internal static readonly string[] LoremWords =
        {
            "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "do",
            "eiusmod", "tempor", "incididunt", "ut", "labore", "et", "dolore", "magna", "aliqua", "enim",
            "ad", "minim", "veniam", "quis", "nostrud", "exercitation", "ullamco", "laboris", "nisi",
            "aliquip", "ex", "ea", "commodo", "consequat", "duis", "aute", "irure", "in", "reprehenderit",
            "voluptate", "velit", "esse", "cillum", "fugiat", "nulla", "pariatur", "excepteur", "sint",
            "occaecat", "cupidatat", "non", "proident", "sunt", "culpa", "qui", "officia", "deserunt",
            "mollit", "anim", "id", "est", "laborum",
        };

        internal static readonly string[] UrlPaths =
        {
            "home", "about", "blog", "products", "pricing", "contact", "docs", "support", "news", "shop",
            "account", "login", "search", "help", "careers", "events",
        };

        internal static readonly Pool English = new()
        {
            FirstNames = new[]
            {
                "James", "Mary", "John", "Patricia", "Robert", "Jennifer", "Michael", "Linda", "William", "Elizabeth",
                "David", "Barbara", "Richard", "Susan", "Joseph", "Jessica", "Thomas", "Sarah", "Charles", "Karen",
                "Daniel", "Nancy", "Matthew", "Lisa", "Anthony", "Betty", "Mark", "Margaret", "Paul", "Sandra",
                "Steven", "Ashley", "Andrew", "Emily", "Kenneth", "Donna", "Joshua", "Michelle", "Kevin", "Carol",
                "Brian", "Amanda", "George", "Melissa", "Edward", "Deborah", "Ronald", "Laura", "Timothy", "Rachel",
                "Ethan", "Olivia", "Noah", "Emma", "Liam", "Ava", "Mason", "Sophia", "Lucas", "Grace",
            },
            LastNames = new[]
            {
                "Smith", "Johnson", "Williams", "Brown", "Jones", "Miller", "Davis", "Wilson", "Anderson", "Taylor",
                "Thomas", "Moore", "Jackson", "Martin", "Lee", "Thompson", "White", "Harris", "Clark", "Lewis",
                "Robinson", "Walker", "Young", "Allen", "King", "Wright", "Scott", "Hill", "Green", "Adams",
                "Baker", "Nelson", "Carter", "Mitchell", "Roberts", "Turner", "Phillips", "Campbell", "Parker", "Evans",
                "Edwards", "Collins", "Stewart", "Morris", "Murphy", "Cook", "Rogers", "Morgan", "Cooper", "Reed",
            },
            Companies = new[]
            {
                "Northwind", "Contoso", "Fabrikam", "Acme", "Globex", "Initech", "Umbrella", "Stark", "Wayne", "Hooli",
                "Pioneer", "Summit", "Harbor", "Bright", "Silverline", "Bluebird", "Ironwood", "Redwood", "Crestview", "Meridian",
                "Alpine", "Orbit", "Vertex", "Nimbus", "Quantum", "Evergreen", "Lakeside", "Granite", "Beacon", "Horizon",
            },
            CompanySuffixes = new[] { "Inc.", "LLC", "Corp.", "Group", "Labs", "Partners", "Systems", "Solutions", "Industries", "& Co." },
            JobTitles = new[]
            {
                "Software Engineer", "Product Manager", "Data Analyst", "Designer", "Accountant", "Sales Representative",
                "Marketing Specialist", "Project Manager", "Customer Support Agent", "Operations Manager", "HR Coordinator",
                "Systems Administrator", "Business Analyst", "QA Tester", "Office Manager", "Technical Writer",
                "Financial Analyst", "Network Engineer", "Account Executive", "Research Scientist",
            },
            StreetNames = new[]
            {
                "Maple", "Oak", "Cedar", "Pine", "Elm", "Washington", "Lake", "Hill", "Sunset", "Park",
                "Main", "Church", "River", "Spring", "Highland", "Franklin", "Jefferson", "Lincoln", "Willow", "Meadow",
            },
            StreetKinds = new[] { "Street", "Avenue", "Road", "Lane", "Drive", "Boulevard", "Court", "Way" },
            Cities = new[]
            {
                "Springfield", "Riverton", "Fairview", "Greenville", "Franklin", "Clinton", "Madison", "Georgetown", "Salem", "Ashland",
                "Burlington", "Oxford", "Kingston", "Milton", "Newport", "Hudson", "Dayton", "Lexington", "Marion", "Auburn",
            },
            Countries = new[] { "United States", "Canada", "United Kingdom", "Australia", "Ireland", "New Zealand", "Germany", "France", "Spain", "Italy", "Japan", "Brazil", "Mexico", "India" },
            PhoneFormat = "+1 (555) 01##-####",
            PostalFormat = "#####",
            NumberBeforeStreet = true,
        };

        internal static readonly Pool Spanish = new()
        {
            FirstNames = new[]
            {
                "Antonio", "María", "José", "Carmen", "Manuel", "Ana", "Francisco", "Laura", "Juan", "Isabel",
                "David", "Lucía", "Javier", "Marta", "Daniel", "Elena", "Carlos", "Paula", "Miguel", "Sara",
                "Alejandro", "Cristina", "Rafael", "Patricia", "Pedro", "Rosa", "Ángel", "Beatriz", "Sergio", "Pilar",
                "Pablo", "Raquel", "Luis", "Alba", "Jorge", "Noelia", "Fernando", "Irene", "Diego", "Silvia",
                "Andrés", "Natalia", "Adrián", "Verónica", "Rubén", "Claudia", "Iván", "Julia", "Gabriel", "Valentina",
            },
            LastNames = new[]
            {
                "García", "Rodríguez", "González", "Fernández", "López", "Martínez", "Sánchez", "Pérez", "Gómez", "Martín",
                "Jiménez", "Ruiz", "Hernández", "Díaz", "Moreno", "Muñoz", "Álvarez", "Romero", "Alonso", "Gutiérrez",
                "Navarro", "Torres", "Domínguez", "Vázquez", "Ramos", "Gil", "Ramírez", "Serrano", "Blanco", "Molina",
                "Castro", "Ortega", "Rubio", "Marín", "Sanz", "Iglesias", "Medina", "Cortés", "Garrido", "Santos",
                "Castillo", "Guerrero", "Lozano", "Cano", "Prieto", "Méndez", "Cruz", "Calvo", "Gallego", "Vidal",
            },
            Companies = new[]
            {
                "Aurora", "Horizonte", "Iberia", "Solaris", "Mediterráneo", "Andina", "Pacífica", "Norte", "Cóndor", "Atlántica",
                "Delta", "Estrella", "Faro", "Ibérica", "Lumbre", "Monte Verde", "Nova", "Olivo", "Puerto Azul", "Roble",
                "Sierra", "Tramontana", "Unión", "Valle", "Zenit", "Centauro", "Granada", "Brújula", "Marea", "Cumbre",
            },
            CompanySuffixes = new[] { "S.A.", "S.L.", "y Asociados", "Grupo", "Soluciones", "Servicios", "Consultores", "Industrias", "Tecnología", "Hermanos" },
            JobTitles = new[]
            {
                "Ingeniero de software", "Gerente de producto", "Analista de datos", "Diseñador", "Contador", "Representante de ventas",
                "Especialista de marketing", "Jefe de proyecto", "Agente de atención al cliente", "Director de operaciones", "Coordinador de RR. HH.",
                "Administrador de sistemas", "Analista de negocio", "Probador de calidad", "Administrativo", "Redactor técnico",
                "Analista financiero", "Ingeniero de redes", "Ejecutivo de cuentas", "Investigador científico",
            },
            StreetNames = new[]
            {
                "Mayor", "de la Paz", "del Sol", "de Cervantes", "de Goya", "de la Constitución", "Real", "de San Miguel", "Nueva", "del Carmen",
                "de la Iglesia", "de Colón", "de Bolívar", "de la Rosa", "del Pilar", "de los Olivos", "de la Libertad", "del Mar", "de Alcalá", "de la Luna",
            },
            StreetKinds = new[] { "Calle", "Avenida", "Paseo", "Plaza", "Camino", "Ronda", "Travesía" },
            Cities = new[]
            {
                "Madrid", "Barcelona", "Valencia", "Sevilla", "Zaragoza", "Málaga", "Murcia", "Bilbao", "Alicante", "Córdoba",
                "Valladolid", "Granada", "Salamanca", "Oviedo", "Santander", "Pamplona", "Toledo", "León", "Burgos", "Cádiz",
            },
            Countries = new[] { "España", "México", "Argentina", "Colombia", "Chile", "Perú", "Venezuela", "Ecuador", "Uruguay", "Estados Unidos", "Portugal", "Francia", "Italia", "Alemania" },
            PhoneFormat = "+34 6## ### ###",
            PostalFormat = "#####",
            NumberBeforeStreet = false,
        };

        internal static Pool PoolFor(MockDataLocale locale) => locale == MockDataLocale.Spanish ? Spanish : English;
    }
}
