/** The ProblemDetails (RFC 9457) body the BFF and the services answer with on an error. */
export interface ProblemDetails {
  readonly type?: string;
  readonly title?: string;
  readonly status?: number;
  readonly detail?: string;
  /** Field name to its messages, on a validation failure. */
  readonly errors?: Readonly<Record<string, readonly string[]>>;
}

/** An answer with an error status. The subclasses say what the page can do about it. */
export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails;

  constructor(status: number, problem: ProblemDetails) {
    super(problem.detail ?? problem.title ?? `A requisição falhou com o status ${String(status)}.`);
    this.name = new.target.name;
    this.status = status;
    this.problem = problem;
  }

  /** Every message of the failure: the field errors when there are some, else the detail or title. */
  get messages(): readonly string[] {
    const fields = Object.values(this.problem.errors ?? {}).flat();

    return fields.length > 0 ? fields : [this.message];
  }
}

/** 400 or 422: the request is wrong; the messages say what to fix. */
export class ValidationError extends ApiError {}

/** 401: there is no session. The client has already sent the browser to the sign-in page. */
export class UnauthorizedError extends ApiError {}

/** 403: signed in without the role the route needs. */
export class ForbiddenError extends ApiError {}

/** 404. */
export class NotFoundError extends ApiError {}

/** 409: for example a discount that was already used. */
export class ConflictError extends ApiError {}

/** 412 or 428: the version in If-Match is stale or missing; reload before trying again. */
export class PreconditionFailedError extends ApiError {}

/** 502, 503 or 504: a service behind the BFF did not answer. */
export class ServiceUnavailableError extends ApiError {}

/** Any other 5xx. */
export class ServerError extends ApiError {}

/** The request never got an answer: offline, reset or timed out. */
export class NetworkError extends Error {
  constructor(options?: ErrorOptions) {
    super('Não foi possível acessar o servidor.', options);
    this.name = 'NetworkError';
  }
}

export function toApiError(status: number, problem: ProblemDetails): ApiError {
  switch (status) {
    case 400:
    case 422:
      return new ValidationError(status, problem);
    case 401:
      return new UnauthorizedError(status, problem);
    case 403:
      return new ForbiddenError(status, problem);
    case 404:
      return new NotFoundError(status, problem);
    case 409:
      return new ConflictError(status, problem);
    case 412:
    case 428:
      return new PreconditionFailedError(status, problem);
    case 502:
    case 503:
    case 504:
      return new ServiceUnavailableError(status, problem);
    default:
      return status >= 500 ? new ServerError(status, problem) : new ApiError(status, problem);
  }
}
