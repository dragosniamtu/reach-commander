import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  SubtitleCapabilities,
  SubtitleJobRequest,
  SubtitleJobStatus,
} from './subtitle-generation.models';

@Injectable({ providedIn: 'root' })
export class SubtitleGenerationApi {
  private readonly http = inject(HttpClient);

  capabilities(): Promise<SubtitleCapabilities> {
    return firstValueFrom(this.http.get<SubtitleCapabilities>('/api/subtitle-jobs/capabilities'));
  }

  list(): Promise<readonly SubtitleJobStatus[]> {
    return firstValueFrom(this.http.get<SubtitleJobStatus[]>('/api/subtitle-jobs'));
  }

  create(request: SubtitleJobRequest): Promise<SubtitleJobStatus> {
    return firstValueFrom(this.http.post<SubtitleJobStatus>('/api/subtitle-jobs', request));
  }

  cancel(jobId: string): Promise<SubtitleJobStatus> {
    return firstValueFrom(
      this.http.post<SubtitleJobStatus>(
        `/api/subtitle-jobs/${encodeURIComponent(jobId)}/cancel`,
        null,
      ),
    );
  }
}
