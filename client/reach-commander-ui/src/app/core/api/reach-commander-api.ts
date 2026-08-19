import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { CommanderApiPort, FileEntryDto, SourceDto, SystemMetricsDto } from './api.models';

@Injectable({ providedIn: 'root' })
export class ReachCommanderApi extends CommanderApiPort {
  constructor(private readonly http: HttpClient) {
    super();
  }

  getSystemMetrics(): Promise<SystemMetricsDto> {
    return firstValueFrom(this.http.get<SystemMetricsDto>('/api/system-metrics'));
  }

  getSources(): Promise<readonly SourceDto[]> {
    return firstValueFrom(this.http.get<readonly SourceDto[]>('/api/sources'));
  }

  listFiles(sourceId: string, path: string): Promise<readonly FileEntryDto[]> {
    const params = new HttpParams()
      .set('sourceId', sourceId)
      .set('path', path);
    return firstValueFrom(
      this.http.get<readonly FileEntryDto[]>('/api/files', { params }),
    );
  }

  getInfo(sourceId: string, path: string): Promise<FileEntryDto> {
    const params = new HttpParams()
      .set('sourceId', sourceId)
      .set('path', path);
    return firstValueFrom(
      this.http.get<FileEntryDto>('/api/files/info', { params }),
    );
  }
}
