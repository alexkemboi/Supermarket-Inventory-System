import { Routes } from '@angular/router';
import { guestOnly, requireAuth } from './auth.guard';
import { AuthPage } from './auth-page';
import { Board } from './board';
import { Inventory } from './inventory';

export const routes: Routes = [
  { path: 'login', component: AuthPage, canActivate: [guestOnly], data: { mode: 'login' } },
  { path: 'signup', component: AuthPage, canActivate: [guestOnly], data: { mode: 'signup' } },
  { path: 'inventory', component: Inventory, canActivate: [requireAuth] },
  { path: 'board', component: Board, canActivate: [requireAuth] },
  { path: '', pathMatch: 'full', redirectTo: 'inventory' },
  { path: '**', redirectTo: 'inventory' },
];
