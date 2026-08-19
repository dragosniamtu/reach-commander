import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideRouter } from '@angular/router';

import { routes } from './app.routes';
import { CommanderApiPort } from './core/api/api.models';
import { ReachCommanderApi } from './core/api/reach-commander-api';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(),
    { provide: CommanderApiPort, useExisting: ReachCommanderApi },
  ]
};
