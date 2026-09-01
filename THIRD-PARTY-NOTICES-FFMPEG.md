# FFmpeg third-party notice

ReachCommander container images include the unmodified Alpine Linux `ffmpeg`
package so the server can inspect video streams and create temporary,
browser-compatible HLS previews.

- Package: `ffmpeg` `6.1.2-r2`
- Alpine branch/repository: `v3.22/community`
- Architectures used by ReachCommander: `x86_64` and `aarch64`
- Upstream project: https://ffmpeg.org/
- Alpine package metadata: https://pkgs.alpinelinux.org/package/v3.22/community/x86_64/ffmpeg
- Package license expression: `GPL-2.0-or-later AND LGPL-2.1-or-later`

ReachCommander's MIT license applies only to ReachCommander. FFmpeg and its
linked libraries remain under their respective licenses. The complete GPL 2.0
and LGPL 2.1 license texts are available from the FFmpeg project at
https://ffmpeg.org/legal.html and from the SPDX license list.

## Corresponding Source

The Alpine package was built from the Alpine `aports` `ffmpeg` package recipe
at commit `19c99e366c9185609249108011f9f621c66f204e`. Its recipe, patches,
checksums, upstream source reference, and build log are linked from the Alpine
package metadata above. The upstream FFmpeg source is also available from
https://ffmpeg.org/releases/.

For at least three years after a ReachCommander image containing this package
is distributed, anyone who cannot obtain that exact Corresponding Source from
the referenced public locations may open an issue at
https://github.com/dragosniamtu/reach-commander/issues. A machine-readable copy
will be provided at no more than the reasonable cost of physically providing
the source.
