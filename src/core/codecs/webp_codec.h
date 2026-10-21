#ifndef UNPACKRAT_CORE_CODECS_WEBP_CODEC_H
#define UNPACKRAT_CORE_CODECS_WEBP_CODEC_H

#include "image_types.h"

#include <functional>
#include <span>

namespace unpackrat::codecs
{
    // Decodes a complete still WebP stream (lossy, lossless or with alpha) into an 8-bit RGBA
    // image. Animated streams are not supported and are rejected like any other unsupported
    // layout. The encoded size is checked against max_input_bytes and the header dimensions
    // against max_pixels/max_output_bytes before the decoded buffer is allocated. Malformed
    // input or a breached limit throws image_error.
    [[nodiscard]] rgba_image decode_webp(std::span<const std::byte> encoded, const image_limits &limits = {});

    // Encodes one RGBA image as an exact lossless WebP stream and delivers it to sink. Geometry,
    // the raw input budget and the pixel-buffer size are validated before encoding, which bounds
    // the encoder's input. The encoded output is checked against max_output_bytes before it is
    // published to sink. Library resources are released through RAII, and a sink exception
    // propagates unchanged.
    void encode_webp_lossless(const rgba_image &image, const std::function<void(std::span<const std::byte>)> &sink,
                              const image_limits &limits = {});
} // namespace unpackrat::codecs
#endif
