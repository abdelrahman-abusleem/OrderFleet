namespace OrderFleet.Application.Errors;

public sealed class RequestValidationException(string message) : Exception(message);
public sealed class ResourceNotFoundException : Exception;
public sealed class AccessDeniedException : Exception;
public sealed class OperationConflictException(string message) : Exception(message);
public sealed class DuplicateRequestException : Exception;
