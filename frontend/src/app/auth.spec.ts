import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, provideRouter, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { guestOnly, requireAuth } from './auth.guard';
import { authInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';
import { AuthResponse } from './models';

const response = (minutesValid = 60): AuthResponse => ({
  token: 'jwt-token',
  expiresAt: new Date(Date.now() + minutesValid * 60_000).toISOString(),
  user: { id: 7, name: 'Amina Otieno', email: 'amina@example.com' },
});

describe('authentication', () => {
  let http: HttpTestingController;
  let auth: AuthService;
  let client: HttpClient;
  let router: Router;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    auth = TestBed.inject(AuthService);
    client = TestBed.inject(HttpClient);
    router = TestBed.inject(Router);
    spyOn(router, 'navigate').and.resolveTo(true);
  });

  afterEach(() => {
    http.verify();
    localStorage.clear();
  });

  const signIn = (minutesValid = 60) => {
    auth.login('amina@example.com', 'secret-pass').subscribe();
    http.expectOne('/api/auth/login').flush(response(minutesValid));
  };

  it('stores the session after login and exposes the user', () => {
    signIn();

    expect(auth.isAuthenticated()).toBeTrue();
    expect(auth.user()?.name).toBe('Amina Otieno');
    expect(localStorage.getItem('rapid-inventory.session')).toContain('jwt-token');
  });

  it('does not send a token with the login request itself', () => {
    auth.login('amina@example.com', 'secret-pass').subscribe();

    const request = http.expectOne('/api/auth/login');
    expect(request.request.headers.has('Authorization')).toBeFalse();
    expect(request.request.body).toEqual({ email: 'amina@example.com', password: 'secret-pass' });
    request.flush(response());
  });

  it('adds the bearer token to API requests once signed in', () => {
    signIn();

    client.get('/api/products').subscribe();

    expect(http.expectOne('/api/products').request.headers.get('Authorization')).toBe('Bearer jwt-token');
  });

  it('treats an expired session as signed out', () => {
    signIn(-1);

    expect(auth.isAuthenticated()).toBeFalse();
    expect(auth.user()).toBeNull();
  });

  it('signs out and returns to the login page when the API answers 401', () => {
    signIn();

    client.get('/api/products').subscribe({ error: () => undefined });
    http.expectOne('/api/products').flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(auth.isAuthenticated()).toBeFalse();
    expect(router.navigate).toHaveBeenCalledWith(['/login']);
  });

  it('keeps the session when a login attempt is rejected', () => {
    auth.login('amina@example.com', 'wrong').subscribe({ error: () => undefined });
    http.expectOne('/api/auth/login').flush({ error: 'Invalid email or password.' }, { status: 401, statusText: 'Unauthorized' });

    expect(router.navigate).not.toHaveBeenCalled();
  });

  describe('guards', () => {
    const run = (guard: typeof requireAuth, url = '/inventory') =>
      TestBed.runInInjectionContext(() => guard({} as ActivatedRouteSnapshot, { url } as RouterStateSnapshot));

    it('sends signed-out users to login with a return URL', () => {
      const result = run(requireAuth, '/board') as UrlTree;

      expect(router.serializeUrl(result)).toBe('/login?returnUrl=%2Fboard');
    });

    it('lets signed-in users into protected pages and away from the login page', () => {
      signIn();

      expect(run(requireAuth)).toBeTrue();
      expect(router.serializeUrl(run(guestOnly) as UrlTree)).toBe('/inventory');
    });
  });
});
