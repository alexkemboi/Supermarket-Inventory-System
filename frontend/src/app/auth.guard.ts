import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/** Pages that need a signed-in user; others are sent to /login and returned afterwards. */
export const requireAuth: CanActivateFn = (_route, state) =>
  inject(AuthService).isAuthenticated()
    || inject(Router).createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });

/** Login and sign-up pages; a signed-in user goes straight to the ledger. */
export const guestOnly: CanActivateFn = () =>
  !inject(AuthService).isAuthenticated() || inject(Router).createUrlTree(['/inventory']);
