#ifndef UNPACKRAT_CORE_WOLFRPG_GAME_CONTEXT_VENDOR_ADAPTER_H
#define UNPACKRAT_CORE_WOLFRPG_GAME_CONTEXT_VENDOR_ADAPTER_H

// Private adapter implementation contract. Include only from game_context.cpp and its tests;
// application/parser interfaces use game_context.h and never see vendor vocabulary.
#include "game_context.h"
#include <uberwolf/GameContext.hpp>

namespace unpackrat::wolfrpg::detail
{
    // Pure value conversion, retaining the best paths present even on a partial/failure result.
    [[nodiscard]] game_context_result convert_vendor_context(const wolf::game::Context &context);
} // namespace unpackrat::wolfrpg::detail
#endif
