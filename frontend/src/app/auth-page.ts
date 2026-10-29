import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { AuthService } from './auth.service';

type Mode = 'login' | 'signup';

/** Sign-in and account creation; /login and /signup share this page and differ by route data. */
@Component({
  selector: 'app-auth-page',
  imports: [FormsModule, RouterLink],
  templateUrl: './auth-page.html',
  styleUrl: './auth-page.css',
})
export class AuthPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly mode: Mode = this.route.snapshot.data['mode'] === 'signup' ? 'signup' : 'login';
  readonly busy = signal(false);
  readonly error = signal('');

  name = '';
  email = '';
  password = '';
  confirmPassword = '';

  submit(): void {
    this.error.set('');
    if (this.mode === 'signup') {
      if (!this.name.trim()) return this.error.set('Enter your name.');
      if (this.password.length < 8) return this.error.set('Password must be at least 8 characters.');
      if (this.password !== this.confirmPassword) return this.error.set('Passwords do not match.');
    }
    if (!this.email.trim() || !this.password) return this.error.set('Enter your email and password.');

    const request = this.mode === 'signup'
      ? this.auth.register(this.name.trim(), this.email.trim(), this.password)
      : this.auth.login(this.email.trim(), this.password);

    this.busy.set(true);
    request.pipe(finalize(() => this.busy.set(false))).subscribe({
      next: () => void this.router.navigateByUrl(this.returnUrl()),
      error: (error: unknown) => this.error.set(messageFor(error, this.mode)),
    });
  }

  private returnUrl(): string {
    const url = this.route.snapshot.queryParamMap.get('returnUrl');
    // Only same-app paths, so the query string cannot redirect to another site.
    return url && url.startsWith('/') && !url.startsWith('//') ? url : '/inventory';
  }
}

function messageFor(error: unknown, mode: Mode): string {
  if (error instanceof HttpErrorResponse) {
    const serverMessage = typeof error.error?.error === 'string' ? error.error.error : '';
    if (serverMessage) return serverMessage;
    if (error.status === 0) return 'Cannot reach the server. Check your connection.';
  }
  return mode === 'signup' ? 'Could not create the account.' : 'Could not sign in.';
}
