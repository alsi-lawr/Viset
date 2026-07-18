#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include <webp/encode.h>

enum {
  WIDTH = 1600,
  HEIGHT = 900,
  FRAME_COUNT = 12,
  SAMPLE_COUNT = 120,
};

static double elapsed_ms(const struct timespec start,
                         const struct timespec end) {
  return (double)(end.tv_sec - start.tv_sec) * 1000.0 +
         (double)(end.tv_nsec - start.tv_nsec) / 1000000.0;
}

static int compare_double(const void *left, const void *right) {
  const double a = *(const double *)left;
  const double b = *(const double *)right;
  return (a > b) - (a < b);
}

static uint8_t *create_frame(const int index) {
  const size_t length = (size_t)WIDTH * HEIGHT * 4;
  uint8_t *rgba = malloc(length);
  if (rgba == NULL)
    return NULL;

  const int moving_panel_left = 180 + index * 31;

  for (int y = 0; y < HEIGHT; ++y) {
    for (int x = 0; x < WIDTH; ++x) {
      const size_t offset = ((size_t)y * WIDTH + x) * 4;
      const int is_header = y < 110;
      const int is_card = x >= 90 && x < 1510 && y >= 155 && y < 825;
      const int is_moving_panel = x >= moving_panel_left &&
                                  x < moving_panel_left + 310 && y >= 260 &&
                                  y < 710;
      const int is_text =
          is_card && y % 62 < 8 && x % 410 > 30 && x % 410 < 330;

      if (is_moving_panel) {
        rgba[offset] = (uint8_t)(37 + index * 3);
        rgba[offset + 1] = (uint8_t)(99 + x % 31);
        rgba[offset + 2] = (uint8_t)(235 - index * 4);
      } else if (is_text) {
        rgba[offset] = 32;
        rgba[offset + 1] = 42;
        rgba[offset + 2] = 62;
      } else if (is_card) {
        rgba[offset] = (uint8_t)(232 - y % 17);
        rgba[offset + 1] = (uint8_t)(238 - x % 13);
        rgba[offset + 2] = 248;
      } else if (is_header) {
        rgba[offset] = (uint8_t)(25 + x % 23);
        rgba[offset + 1] = (uint8_t)(34 + index * 2);
        rgba[offset + 2] = (uint8_t)(56 + y % 19);
      } else {
        rgba[offset] = (uint8_t)(242 - x % 11);
        rgba[offset + 1] = (uint8_t)(246 - y % 9);
        rgba[offset + 2] = (uint8_t)(252 - index);
      }

      const int transparent_corner =
          (x < 40 || x >= WIDTH - 40) && (y < 40 || y >= HEIGHT - 40);
      rgba[offset + 3] = transparent_corner ? 0 : 255;
    }
  }

  return rgba;
}

int main(const int argc, char **argv) {
  if (argc != 6 && argc != 7) {
    fprintf(stderr,
            "usage: %s LOSSLESS METHOD QUALITY THREAD_LEVEL DIRECT_ARGB "
            "[RAW_FRAME]\n",
            argv[0]);
    return 2;
  }

  WebPConfig config;
  if (!WebPConfigInit(&config))
    return 3;
  config.lossless = atoi(argv[1]);
  config.method = atoi(argv[2]);
  config.quality = strtof(argv[3], NULL);
  config.thread_level = atoi(argv[4]);
  const int direct_argb = atoi(argv[5]);
  config.alpha_quality = 100;
  config.exact = 1;
  if (!WebPValidateConfig(&config))
    return 4;

  uint8_t *frames[FRAME_COUNT] = {0};
  for (int index = 0; index < FRAME_COUNT; ++index) {
    if (argc == 7) {
      const size_t length = (size_t)WIDTH * HEIGHT * 4;
      frames[index] = malloc(length);
      FILE *input = fopen(argv[6], "rb");
      if (input == NULL || frames[index] == NULL ||
          fread(frames[index], 1, length, input) != length) {
        return 5;
      }
      fclose(input);
    } else {
      frames[index] = create_frame(index);
    }
    if (frames[index] == NULL)
      return 5;
  }

  double samples[SAMPLE_COUNT] = {0};
  size_t total_size = 0;

  for (int sample = -8; sample < SAMPLE_COUNT; ++sample) {
    WebPPicture picture;
    if (!WebPPictureInit(&picture))
      return 6;
    picture.width = WIDTH;
    picture.height = HEIGHT;
    picture.use_argb = 1;

    WebPMemoryWriter writer;
    WebPMemoryWriterInit(&writer);
    picture.writer = WebPMemoryWrite;
    picture.custom_ptr = &writer;

    const struct timespec start = ({
      struct timespec value;
      clock_gettime(CLOCK_MONOTONIC, &value);
      value;
    });

    const int frame_index = (sample < 0 ? sample + 8 : sample) % FRAME_COUNT;
    if (direct_argb) {
      picture.argb = (uint32_t *)frames[frame_index];
      picture.argb_stride = WIDTH;
    } else if (!WebPPictureImportRGBA(&picture, frames[frame_index],
                                      WIDTH * 4)) {
      return 7;
    }
    if (!WebPEncode(&config, &picture))
      return 8;

    const struct timespec end = ({
      struct timespec value;
      clock_gettime(CLOCK_MONOTONIC, &value);
      value;
    });

    if (sample >= 0) {
      samples[sample] = elapsed_ms(start, end);
      total_size += writer.size;
    }

    WebPMemoryWriterClear(&writer);
    WebPPictureFree(&picture);
  }

  for (int index = 0; index < FRAME_COUNT; ++index)
    free(frames[index]);

  qsort(samples, SAMPLE_COUNT, sizeof(double), compare_double);
  double sum = 0;
  for (int index = 0; index < SAMPLE_COUNT; ++index)
    sum += samples[index];

  printf("lossless=%d method=%d quality=%.0f threads=%d direct=%d mean_ms=%.3f "
         "p50_ms=%.3f "
         "p95_ms=%.3f max_ms=%.3f mean_bytes=%.0f\n",
         config.lossless, config.method, config.quality, config.thread_level,
         direct_argb, sum / SAMPLE_COUNT, samples[SAMPLE_COUNT / 2],
         samples[114], samples[SAMPLE_COUNT - 1],
         (double)total_size / SAMPLE_COUNT);
  return 0;
}
