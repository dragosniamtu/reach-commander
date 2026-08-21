import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { AuthenticationChannel } from './authentication-channel';

const unsafeMethods = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);
const credentialRoutes = new Set([
  '/api/auth/login',
  '/api/auth/setup',
  '/api/auth/password',
]);
const expectedCredentialCodes = new Set(['invalid_credentials', 'setup_failed']);

export const authenticationInterceptor: HttpInterceptorFn = (request, next) => {
  if (!isSameOriginApiRequest(request.url)) {
    return next(request);
  }

  const channel = inject(AuthenticationChannel);
  const token = channel.token();
  const authenticatedRequest = request.clone({
    withCredentials: true,
    ...(token && unsafeMethods.has(request.method.toUpperCase())
      ? { setHeaders: { 'X-ReachCommander-CSRF': token } }
      : {}),
  });

  return next(authenticatedRequest).pipe(
    catchError((error: unknown) => {
      if (
        error instanceof HttpErrorResponse &&
        error.status === 401 &&
        !isExpectedCredentialFailure(authenticatedRequest.url, error)
      ) {
        channel.notifyUnauthorized();
      }

      return throwError(() => error);
    }),
  );
};

function isSameOriginApiRequest(url: string): boolean {
  if (url.startsWith('/api/')) {
    return true;
  }

  try {
    const base = globalThis.location?.origin;
    if (!base) {
      return false;
    }

    const parsed = new URL(url, base);
    return parsed.origin === base && parsed.pathname.startsWith('/api/');
  } catch {
    return false;
  }
}

function isExpectedCredentialFailure(url: string, error: HttpErrorResponse): boolean {
  const path = requestPath(url);
  return (
    credentialRoutes.has(path) &&
    typeof error.error === 'object' &&
    error.error !== null &&
    'code' in error.error &&
    typeof error.error.code === 'string' &&
    expectedCredentialCodes.has(error.error.code)
  );
}

function requestPath(url: string): string {
  if (url.startsWith('/')) {
    return url.split('?', 1)[0];
  }

  try {
    return new URL(url, globalThis.location?.origin ?? 'http://localhost').pathname;
  } catch {
    return url;
  }
}
