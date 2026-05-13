namespace ARM.Integration.Core
{
    public class FOError
    {       
        public Error? error { get; set; }
    }

    public class Internalexception
    {
        public string? message { get; set; }
        public string? type { get; set; }
        public string? stacktrace { get; set; }
    }

    public class Innererror
    {
        public string? message { get; set; }
        public string? type { get; set; }
        public string? stacktrace { get; set; }
        public Internalexception? internalexception { get; set; }
    }

    public class Error
    {
        public string? code { get; set; }
        public string? message { get; set; }
        public Innererror? innererror { get; set; }
    }
}
