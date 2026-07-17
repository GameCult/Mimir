#pragma once

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <map>
#include <utility>
#include <vector>

namespace muninn_video_fec {

inline uint8_t multiply(uint8_t a, uint8_t b)
{
    uint8_t product = 0;
    while (b != 0) {
        if ((b & 1) != 0)
            product ^= a;
        const bool carry = (a & 0x80) != 0;
        a <<= 1;
        if (carry)
            a ^= 0x1d;
        b >>= 1;
    }
    return product;
}

inline uint8_t inverse(uint8_t value)
{
    uint8_t result = 1;
    for (size_t index = 0; index < 254; ++index)
        result = multiply(result, value);
    return result;
}

inline uint8_t coefficient(uint16_t parity_index, uint16_t parity_count, uint16_t data_index)
{
    return inverse(
        static_cast<uint8_t>(parity_index) ^ static_cast<uint8_t>(parity_count + data_index));
}

inline bool recover(std::vector<std::vector<uint8_t>> &chunks,
                    const std::vector<uint32_t> &chunk_lengths,
                    uint16_t parity_count,
                    const std::map<uint16_t, std::vector<uint8_t>> &parity)
{
    if (chunks.size() != chunk_lengths.size() || chunks.empty() ||
        chunks.size() + parity_count > 256)
        return false;
    std::vector<uint16_t> missing;
    for (uint16_t index = 0; index < chunks.size(); ++index) {
        if (chunks[index].empty())
            missing.push_back(index);
    }
    if (missing.empty())
        return true;
    if (missing.size() > parity.size())
        return false;

    std::vector<std::pair<uint16_t, const std::vector<uint8_t> *>> rows;
    for (const auto &shard : parity) {
        if (shard.first < parity_count && !shard.second.empty())
            rows.emplace_back(shard.first, &shard.second);
        if (rows.size() == missing.size())
            break;
    }
    if (rows.size() != missing.size())
        return false;

    size_t shard_bytes = 0;
    for (uint32_t length : chunk_lengths)
        shard_bytes = std::max(shard_bytes, static_cast<size_t>(length));
    if (shard_bytes == 0 || std::any_of(rows.begin(), rows.end(),
            [shard_bytes](const auto &row) { return row.second->size() < shard_bytes; }))
        return false;

    std::vector<std::vector<uint8_t>> recovered;
    recovered.reserve(missing.size());
    for (uint16_t index : missing) {
        if (chunk_lengths[index] == 0 || chunk_lengths[index] > shard_bytes)
            return false;
        recovered.emplace_back(chunk_lengths[index], 0);
    }

    const size_t count = missing.size();
    for (size_t offset = 0; offset < shard_bytes; ++offset) {
        std::vector<std::vector<uint8_t>> matrix(count, std::vector<uint8_t>(count + 1, 0));
        for (size_t row = 0; row < count; ++row) {
            const uint16_t parity_index = rows[row].first;
            uint8_t rhs = (*rows[row].second)[offset];
            for (uint16_t data_index = 0; data_index < chunks.size(); ++data_index) {
                if (!chunks[data_index].empty() && offset < chunks[data_index].size())
                    rhs ^= multiply(coefficient(parity_index, parity_count, data_index),
                                    chunks[data_index][offset]);
            }
            for (size_t column = 0; column < count; ++column)
                matrix[row][column] = coefficient(parity_index, parity_count, missing[column]);
            matrix[row][count] = rhs;
        }
        for (size_t column = 0; column < count; ++column) {
            size_t pivot = column;
            while (pivot < count && matrix[pivot][column] == 0)
                ++pivot;
            if (pivot == count)
                return false;
            std::swap(matrix[column], matrix[pivot]);
            const uint8_t scale = inverse(matrix[column][column]);
            for (size_t cell = column; cell <= count; ++cell)
                matrix[column][cell] = multiply(matrix[column][cell], scale);
            for (size_t row = 0; row < count; ++row) {
                if (row == column)
                    continue;
                const uint8_t factor = matrix[row][column];
                for (size_t cell = column; cell <= count; ++cell)
                    matrix[row][cell] ^= multiply(factor, matrix[column][cell]);
            }
        }
        for (size_t index = 0; index < count; ++index) {
            if (offset < recovered[index].size())
                recovered[index][offset] = matrix[index][count];
        }
    }
    for (size_t index = 0; index < count; ++index)
        chunks[missing[index]] = std::move(recovered[index]);
    return true;
}

} // namespace muninn_video_fec
