// Durable behavioral tests for the shared, globals-free classic Wolf RPG DX-archive kernel
// (wolf::archive::classic::apply_key_xor, UberWolfLib/Archive/ClassicContext.hpp) and for the
// pinned vendor entry points it underpins.
//
// This translation unit links the real pinned uberwolf library and includes vendor headers on
// purpose: it is the vendor-contract layer under the adapter, so it exercises the actual
// DXArchive::KeyConv / DXArchive::KeyCreate / DXArchive::HashCRC32 implementation instead of a
// stand-in. The XOR expectations are the frozen classic bytes the adapter contract is pinned to
// (see wolfrpg_crypto.cpp and the operational oracle in out/sol-team/oracle).
//
// Contract under test:
//   apply_key_xor(data, phase, key) applies the classic 7-byte key XOR and depends only on the
//     explicit ``key`` and ``phase`` -- never on the process-wide crypt-mode flags
//     (g_newCrypt / g_chacha20 / g_cryptVersion) that DXArchive::KeyConv consults first.
//   DXArchive::KeyConv keeps its mode dispatch but its classic branch delegates to the kernel,
//     so a valid classic call is byte-identical to the kernel for every phase.
//   DXArchive::KeyCreate derives the frozen classic keys, including under concurrent first use,
//     because HashCRC32 builds its table once under the C++ static-initialisation guarantee.

#include <uberwolf/Archive/ClassicContext.hpp>
#include <uberwolf/DXLib/DXArchive.h>

#include <array>
#include <atomic>
#include <barrier>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <mutex>
#include <span>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include <catch2/catch_test_macros.hpp>

// The pinned DXArchive.cpp translation unit defines its crypt-mode flags as namespace-scope
// globals with external linkage. Declaring them here lets the tests drive the real library into
// a non-classic mode without inventing a fake backend.
extern bool g_newCrypt;
extern bool g_chacha20;
extern std::uint16_t g_cryptVersion;

namespace
{
    // ---- frozen classic vectors (see the adapter oracle) ----

    constexpr std::array<unsigned char, 7> kKeyPattern4 = {0xCE, 0x61, 0x44, 0x00, 0x23, 0x62, 0x5A};
    constexpr std::array<unsigned char, 7> kKeyPattern1024 = {0x96, 0x87, 0x56, 0xD0, 0xE3, 0xFD, 0x63};
    constexpr std::array<unsigned char, 7> kKeyPattern2039 = {0xD1, 0xB9, 0x86, 0xD0, 0x61, 0x1B, 0xCA};

    constexpr std::array<std::byte, 16> kXorInput = {
        std::byte{0x01}, std::byte{0x02}, std::byte{0x03}, std::byte{0x04}, std::byte{0x05}, std::byte{0x06},
        std::byte{0x07}, std::byte{0x08}, std::byte{0x09}, std::byte{0x0A}, std::byte{0x0B}, std::byte{0x0C},
        std::byte{0x0D}, std::byte{0x0E}, std::byte{0x0F}, std::byte{0x10}};
    constexpr std::array<unsigned char, 7> kXorKey = {0x56, 0xF0, 0x6C, 0xA7, 0x87, 0xCF, 0x0E};
    constexpr std::array<std::byte, 16> kXorExpectedPos0 = {
        std::byte{0x57}, std::byte{0xF2}, std::byte{0x6F}, std::byte{0xA3}, std::byte{0x82}, std::byte{0xC9},
        std::byte{0x09}, std::byte{0x5E}, std::byte{0xF9}, std::byte{0x66}, std::byte{0xAC}, std::byte{0x8B},
        std::byte{0xC2}, std::byte{0x00}, std::byte{0x59}, std::byte{0xE0}};
    constexpr std::array<std::byte, 16> kXorExpectedPos1 = {
        std::byte{0xF1}, std::byte{0x6E}, std::byte{0xA4}, std::byte{0x83}, std::byte{0xCA}, std::byte{0x08},
        std::byte{0x51}, std::byte{0xF8}, std::byte{0x65}, std::byte{0xAD}, std::byte{0x8C}, std::byte{0xC3},
        std::byte{0x03}, std::byte{0x58}, std::byte{0xFF}, std::byte{0x7C}};
    constexpr std::array<std::byte, 16> kXorExpectedPos2 = {
        std::byte{0x6D}, std::byte{0xA5}, std::byte{0x84}, std::byte{0xCB}, std::byte{0x0B}, std::byte{0x50},
        std::byte{0xF7}, std::byte{0x64}, std::byte{0xAE}, std::byte{0x8D}, std::byte{0xC4}, std::byte{0x02},
        std::byte{0x5B}, std::byte{0xFE}, std::byte{0x63}, std::byte{0xB7}};
    constexpr std::array<std::byte, 16> kXorExpectedPos3 = {
        std::byte{0xA6}, std::byte{0x85}, std::byte{0xCC}, std::byte{0x0A}, std::byte{0x53}, std::byte{0xF6},
        std::byte{0x6B}, std::byte{0xAF}, std::byte{0x8E}, std::byte{0xC5}, std::byte{0x05}, std::byte{0x5A},
        std::byte{0xFD}, std::byte{0x62}, std::byte{0xA8}, std::byte{0x97}};
    constexpr std::array<std::byte, 16> kXorExpectedPos4 = {
        std::byte{0x86}, std::byte{0xCD}, std::byte{0x0D}, std::byte{0x52}, std::byte{0xF5}, std::byte{0x6A},
        std::byte{0xA0}, std::byte{0x8F}, std::byte{0xC6}, std::byte{0x04}, std::byte{0x5D}, std::byte{0xFC},
        std::byte{0x61}, std::byte{0xA9}, std::byte{0x88}, std::byte{0xDF}};
    constexpr std::array<std::byte, 16> kXorExpectedPos5 = {
        std::byte{0xCE}, std::byte{0x0C}, std::byte{0x55}, std::byte{0xF4}, std::byte{0x69}, std::byte{0xA1},
        std::byte{0x80}, std::byte{0xC7}, std::byte{0x07}, std::byte{0x5C}, std::byte{0xFB}, std::byte{0x60},
        std::byte{0xAA}, std::byte{0x89}, std::byte{0xC0}, std::byte{0x1E}};
    constexpr std::array<std::byte, 16> kXorExpectedPos6 = {
        std::byte{0x0F}, std::byte{0x54}, std::byte{0xF3}, std::byte{0x68}, std::byte{0xA2}, std::byte{0x81},
        std::byte{0xC8}, std::byte{0x06}, std::byte{0x5F}, std::byte{0xFA}, std::byte{0x67}, std::byte{0xAB},
        std::byte{0x8A}, std::byte{0xC1}, std::byte{0x01}, std::byte{0x46}};

    constexpr std::array<const std::array<std::byte, 16> *, 7> kXorExpectedByPhase = {
        &kXorExpectedPos0, &kXorExpectedPos1, &kXorExpectedPos2, &kXorExpectedPos3,
        &kXorExpectedPos4, &kXorExpectedPos5, &kXorExpectedPos6};

    // ---- helpers ----

    // Restores the process-wide crypt-mode flags on scope exit so a contamination test cannot
    // leak its mode into later cases.
    class mode_guard
    {
      public:
        mode_guard() : new_crypt_(g_newCrypt), chacha20_(g_chacha20), version_(g_cryptVersion)
        {
        }
        ~mode_guard()
        {
            g_newCrypt = new_crypt_;
            g_chacha20 = chacha20_;
            g_cryptVersion = version_;
        }
        mode_guard(const mode_guard &) = delete;
        mode_guard &operator=(const mode_guard &) = delete;

      private:
        bool new_crypt_;
        bool chacha20_;
        std::uint16_t version_;
    };

    // Deterministic, non-trivial material: byte i = (i * 7 + 3) & 0xFF. Identical to the
    // generator the adapter oracle and wolfrpg_crypto.cpp use.
    std::vector<std::byte> patterned(std::size_t length)
    {
        std::vector<std::byte> material(length);
        for (std::size_t i = 0; i < length; ++i)
        {
            material[i] = static_cast<std::byte>((i * 7u + 3u) & 0xFFu);
        }
        return material;
    }

    std::array<unsigned char, 7> key_create(std::span<const std::byte> material)
    {
        std::vector<char> scratch(material.size() + 1, '\0');
        for (std::size_t i = 0; i < material.size(); ++i)
        {
            scratch[i] = static_cast<char>(material[i]);
        }

        std::array<unsigned char, 7> raw{};
        DXArchive::KeyCreate(scratch.data(), material.size(), raw.data());
        return raw;
    }

    // ---- concurrency launch scaffolding ----

    // A barrier's participant count is fixed when it is built, so a participant that never starts
    // - because its thread could not be created - would leave the participants that did start
    // waiting on an arrival that can never come. The cold case below drives several participants
    // into one std::barrier, so the launch is funnelled through a spawn factory that can fail
    // deterministically instead of by exhausting the system. The shape mirrors the accepted
    // Wolf RPG ChaCha harness so a creation failure tears the group down the same way there.

    // A distinct, self-describing failure so a test can prove the launcher rethrows the exact
    // creation exception it was handed rather than swallowing or substituting it.
    class launch_fault final : public std::runtime_error
    {
      public:
        explicit launch_fault(std::size_t participant)
            : std::runtime_error("participant " + std::to_string(participant) + " could not start"),
              participant_(participant)
        {
        }

        [[nodiscard]] std::size_t participant() const noexcept
        {
            return participant_;
        }

      private:
        std::size_t participant_;
    };

    // Holds every started participant just below the barrier until the launcher has created the
    // whole group. A participant told to abort returns false and leaves without ever touching the
    // barrier, so a group that never reached full strength cannot strand its members on an arrival
    // that will never happen.
    class launch_gate
    {
      public:
        // Worker side: block until the launcher commits or aborts. Returns false exactly when the
        // launch was aborted, i.e. when the participant must leave without touching the barrier.
        [[nodiscard]] bool wait()
        {
            std::unique_lock lock(mutex_);
            cv_.wait(lock, [this] { return decided_; });
            return !aborted_;
        }

        void commit()
        {
            decide(false);
        }

        void abort()
        {
            decide(true);
        }

      private:
        void decide(bool aborted)
        {
            {
                std::lock_guard lock(mutex_);
                aborted_ = aborted;
                decided_ = true;
            }
            cv_.notify_all();
        }

        std::mutex mutex_;
        std::condition_variable cv_;
        bool decided_ = false;
        bool aborted_ = false;
    };

    // The default participant handle: a real, joinable thread.
    std::jthread spawn_thread(std::function<void()> task)
    {
        return std::jthread{std::move(task)};
    }

    // Runs worker(index) on worker_count participants that the barrier inside worker expects to
    // meet. spawn creates one participant and must yield a joinable handle; it may throw (the
    // runtime cannot create another thread, or a fault injected by a test). exited counts the
    // participants whose thread body ran to completion.
    //
    // If any spawn throws, the started participants are released from the gate, all started
    // participants are joined, and the original exception is rethrown unchanged. A participant
    // that never started therefore cannot leave its peers waiting at the barrier, and no started
    // participant is destroyed while still joinable.
    template <class Spawn>
    void launch_together(std::size_t worker_count, const std::function<void(std::size_t)> &worker, Spawn spawn,
                         std::atomic<std::size_t> &exited)
    {
        launch_gate gate;
        std::vector<std::jthread> participants;
        participants.reserve(worker_count);
        try
        {
            for (std::size_t index = 0; index < worker_count; ++index)
            {
                participants.push_back(spawn([&gate, &worker, &exited, index] {
                    if (gate.wait())
                    {
                        worker(index);
                    }
                    exited.fetch_add(1, std::memory_order_relaxed);
                }));
            }
        }
        catch (...)
        {
            gate.abort();
            throw; // participants joins every started thread while the exception leaves this frame
        }
        gate.commit();
    } // participants joins every started thread before returning
} // namespace

TEST_CASE("classic kernel reproduces the frozen bytes for every key phase",
          "[uberwolf][classic][context]")
{
    for (std::size_t phase = 0; phase < kXorExpectedByPhase.size(); ++phase)
    {
        INFO("phase " << phase);
        std::array<std::byte, 16> data = kXorInput;
        wolf::archive::classic::apply_key_xor(data, phase, kXorKey);
        CHECK(data == *kXorExpectedByPhase[phase]);
    }
}

TEST_CASE("classic kernel is a no-op on an empty span", "[uberwolf][classic][context]")
{
    std::array<std::byte, 3> data = {std::byte{0x11}, std::byte{0x22}, std::byte{0x33}};

    wolf::archive::classic::apply_key_xor(std::span<std::byte>{}, 0, kXorKey);
    wolf::archive::classic::apply_key_xor(std::span<std::byte>{}, 6, kXorKey);

    CHECK(data[0] == std::byte{0x11});
    CHECK(data[1] == std::byte{0x22});
    CHECK(data[2] == std::byte{0x33});
}

TEST_CASE("classic kernel ignores the process-wide crypt-mode flags", "[uberwolf][classic][context]")
{
    struct mode_case
    {
        const char *name;
        bool new_crypt;
        bool chacha20;
        std::uint16_t version;
    };

    const std::array<mode_case, 2> modes = {{
        {"new crypt v3.50", true, false, 350},
        {"ChaCha20 (0x64)", false, true, 0x64},
    }};

    for (const mode_case &mode : modes)
    {
        INFO(mode.name);
        mode_guard guard;
        g_newCrypt = mode.new_crypt;
        g_chacha20 = mode.chacha20;
        g_cryptVersion = mode.version;

        std::array<std::byte, 16> kernel_data = kXorInput;
        wolf::archive::classic::apply_key_xor(kernel_data, 0, kXorKey);
        CHECK(kernel_data == kXorExpectedPos0);

        // Control: the flags are live. DXArchive::KeyConv still dispatches on them, so its
        // classic-looking call must NOT produce the classic bytes while the mode is set.
        std::array<unsigned char, 7> key = kXorKey;
        std::array<std::byte, 16> vendor_data = kXorInput;
        DXArchive::KeyConv(vendor_data.data(), static_cast<s64>(vendor_data.size()), 0, key.data());
        CHECK(vendor_data != kXorExpectedPos0);
    }
}

TEST_CASE("classic KeyConv branch delegates to the shared kernel for every phase",
          "[uberwolf][classic][context]")
{
    // In the default mode the vendor classic branch and the kernel must be byte-identical: the
    // delegation in DXArchive::KeyConv is not allowed to change a single classic byte.
    mode_guard guard;
    g_newCrypt = false;
    g_chacha20 = false;
    g_cryptVersion = 0;

    std::array<unsigned char, 7> key = kXorKey;

    for (std::size_t phase = 0; phase < kXorExpectedByPhase.size(); ++phase)
    {
        INFO("phase " << phase);

        std::array<std::byte, 16> vendor_data = kXorInput;
        DXArchive::KeyConv(vendor_data.data(), static_cast<s64>(vendor_data.size()),
                           static_cast<s64>(phase), key.data());

        std::array<std::byte, 16> kernel_data = kXorInput;
        wolf::archive::classic::apply_key_xor(kernel_data, phase, kXorKey);

        CHECK(vendor_data == *kXorExpectedByPhase[phase]);
        CHECK(vendor_data == kernel_data);
    }
}

TEST_CASE("classic KeyConv classic branch treats a non-positive extent as a no-op",
          "[uberwolf][classic][context]")
{
    mode_guard guard;
    g_newCrypt = false;
    g_chacha20 = false;
    g_cryptVersion = 0;

    std::array<unsigned char, 7> key = kXorKey;
    std::array<std::byte, 16> data = kXorInput;

    DXArchive::KeyConv(data.data(), 0, 3, key.data());
    DXArchive::KeyConv(data.data(), -1, 3, key.data());
    DXArchive::KeyConv(data.data(), static_cast<s64>(data.size()), 0, nullptr);

    CHECK(data == kXorInput);
}

TEST_CASE("direct KeyCreate derives the frozen classic keys", "[uberwolf][classic][context]")
{
    const std::vector<std::byte> material = patterned(4);
    CHECK(key_create(material) == kKeyPattern4);
    CHECK(key_create(patterned(1024)) == kKeyPattern1024);
    CHECK(key_create(patterned(2039)) == kKeyPattern2039);

    // A zero-length source must not be forwarded as a null pointer; the library falls back to
    // its DefaultKeyString and still returns a deterministic key.
    const std::array<unsigned char, 7> empty_key = key_create(std::span<const std::byte>{});
    CHECK(empty_key == key_create(std::span<const std::byte>{}));
}

TEST_CASE("direct KeyCreate is stable across concurrent first use in a fresh process",
          "[uberwolf][classic][context][cold]")
{
    // The CRC table lives behind a function-local static, so the first HashCRC32 in a process is
    // safe to contend. Run this case in a filtered fresh process so that first build is genuinely
    // simultaneous; every thread must observe the frozen key for its material.
    constexpr std::size_t kThreads = 8;
    constexpr std::size_t kIterations = 64;

    struct derive_case
    {
        std::size_t length;
        std::array<unsigned char, 7> expected;
        const char *name;
    };

    const std::array<derive_case, 3> cases = {{
        {4, kKeyPattern4, "length 4"},
        {1024, kKeyPattern1024, "length 1024"},
        {2039, kKeyPattern2039, "length 2039"},
    }};

    std::array<std::array<unsigned char, 7>, kThreads * cases.size()> results{};
    std::atomic<std::size_t> failures{0};
    std::barrier rendezvous(static_cast<std::ptrdiff_t>(kThreads));

    const auto derive_all = [&cases, &results, &failures, &rendezvous](std::size_t t) {
        rendezvous.arrive_and_wait();
        for (std::size_t iter = 0; iter < kIterations; ++iter)
        {
            for (std::size_t c = 0; c < cases.size(); ++c)
            {
                const std::array<unsigned char, 7> derived = key_create(patterned(cases[c].length));
                results[t * cases.size() + c] = derived;
                if (derived != cases[c].expected)
                {
                    failures.fetch_add(1, std::memory_order_relaxed);
                }
            }
        }
    };

    std::atomic<std::size_t> workers_exited{0};
    launch_together(kThreads, derive_all, spawn_thread, workers_exited);
    REQUIRE(workers_exited.load() == kThreads);

    CHECK(failures.load() == 0);
    for (std::size_t t = 0; t < kThreads; ++t)
    {
        for (std::size_t c = 0; c < cases.size(); ++c)
        {
            INFO("thread " << t << " case " << cases[c].name);
            CHECK(results[t * cases.size() + c] == cases[c].expected);
        }
    }
}

TEST_CASE("a participant that cannot start releases the others without deadlocking",
          "[uberwolf][classic][context][cold][launch]")
{
    // The same barrier shape as the cold concurrency case: every participant must arrive, so a
    // participant that is never created would strand the started ones at the barrier forever. A
    // launcher that merely joins the started threads on its way out of a creation failure turns
    // that stall into a std::terminate when the still-joinable threads are destroyed on unwinding.
    constexpr std::size_t kThreads = 8;
    constexpr std::size_t kIterations = 8;

    std::barrier rendezvous(static_cast<std::ptrdiff_t>(kThreads));
    // Counts completed CRC work; a released participant never reaches it, so an aborted launch
    // must leave this at zero.
    std::atomic<std::size_t> derivations{0};
    const auto derive_rounds = [&rendezvous, &derivations](std::size_t) {
        for (std::size_t round = 0; round < kIterations; ++round)
        {
            rendezvous.arrive_and_wait();
            (void)key_create(patterned(4));
        }
        derivations.fetch_add(1, std::memory_order_relaxed);
    };

    // Deterministic, exhaustion-free controls: participant 1, then 2, then 3, then nearly the
    // whole group (7) cannot start, so 1, 2, 3 and 7 participants respectively must each be
    // released and joined rather than stranded at the barrier.
    for (const std::size_t fail_at : {std::size_t{1}, std::size_t{2}, std::size_t{3}, std::size_t{7}})
    {
        INFO("participant " << fail_at << " of " << kThreads << " cannot start");

        std::size_t created = 0;
        const auto failing_spawn = [&created, fail_at](std::function<void()> task) -> std::jthread {
            if (created == fail_at)
            {
                throw launch_fault{fail_at};
            }
            ++created;
            return std::jthread{std::move(task)};
        };

        std::atomic<std::size_t> exited{0};
        bool threw = false;
        try
        {
            launch_together(kThreads, derive_rounds, failing_spawn, exited);
        }
        catch (const launch_fault &fault)
        {
            threw = true;
            // The launcher must surface the exact creation failure it was handed, not a substitute.
            CHECK(fault.participant() == fail_at);
        }

        CHECK(threw);
        // Exactly the participants that did start were joined; none was left waiting on the
        // barrier, and the failed participant was never created.
        CHECK(created == fail_at);
        CHECK(exited.load() == fail_at);
        // No started participant entered the barrier, so no CRC work was ever done.
        CHECK(derivations.load() == 0);
    }

    // Positive control: with a spawn that never fails, all eight participants run and the barrier
    // completes every round, so the counters above track participation rather than a constant.
    {
        std::size_t created = 0;
        const auto counting_spawn = [&created](std::function<void()> task) -> std::jthread {
            ++created;
            return std::jthread{std::move(task)};
        };
        std::atomic<std::size_t> exited{0};
        launch_together(kThreads, derive_rounds, counting_spawn, exited);
        CHECK(created == kThreads);
        CHECK(exited.load() == kThreads);
        CHECK(derivations.load() == kThreads);
    }
}
