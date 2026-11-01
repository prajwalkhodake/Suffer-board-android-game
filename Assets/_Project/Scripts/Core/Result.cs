namespace Skyrift.Core
{
    /// <summary>
    /// Outcome of a fallible operation (TECH_DOC §4.0). Expected failures are values, not exceptions:
    /// a failed purchase or a refused continue must never unwind the stack through gameplay.
    /// </summary>
    public readonly struct Result
    {
        public bool IsSuccess { get; }
        public string ErrorCode { get; }
        public string Message { get; }

        private Result(bool success, string code, string message)
        {
            IsSuccess = success;
            ErrorCode = code ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public static Result Ok() => new Result(true, null, null);
        public static Result Fail(string code, string message = null) => new Result(false, code, message);

        public override string ToString() => IsSuccess ? "Ok" : $"Fail({ErrorCode}: {Message})";
    }

    /// <summary>Typed result. <see cref="Value"/> is only meaningful when <see cref="Result.IsSuccess"/>.</summary>
    public readonly struct Result<T>
    {
        public bool IsSuccess { get; }
        public string ErrorCode { get; }
        public string Message { get; }
        public T Value { get; }

        private Result(bool success, T value, string code, string message)
        {
            IsSuccess = success;
            Value = value;
            ErrorCode = code ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public static Result<T> Ok(T value) => new Result<T>(true, value, null, null);
        public static Result<T> Fail(string code, string message = null) => new Result<T>(false, default, code, message);

        public Result WithoutValue() => IsSuccess ? Result.Ok() : Result.Fail(ErrorCode, Message);

        public override string ToString() => IsSuccess ? $"Ok({Value})" : $"Fail({ErrorCode}: {Message})";
    }

    /// <summary>Well-known error codes. Extend additively; never rename a shipped code (analytics depend on it).</summary>
    public static class ErrorCodes
    {
        public const string InvalidArgument = "invalid_argument";
        public const string NotFound = "not_found";
        public const string AlreadyActive = "already_active";
        public const string NotAvailable = "not_available";
        public const string InsufficientFunds = "insufficient_funds";
        public const string Cancelled = "cancelled";
        public const string Timeout = "timeout";
        public const string CatalogInvalid = "catalog_invalid";
        public const string PoolMissing = "pool_missing";

        /// <summary>An unexpected failure. Always paired with a logged exception detail, never shown raw to the player.</summary>
        public const string SystemError = "system_error";
    }
}
