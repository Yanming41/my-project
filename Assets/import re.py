import re
import argparse
from datetime import datetime, timedelta

# 匹配字幕时间格式，如 00:01:23,456 --> 00:01:25,678
TIMESTAMP_PATTERN = re.compile(r"(\d{2}:\d{2}:\d{2},\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2},\d{3})")


def shift_timestamp(timestamp: str, delta: timedelta) -> str:
    """
    将单个时间戳字符串加上时间偏移量，返回新的时间戳字符串
    """
    dt = datetime.strptime(timestamp, "%H:%M:%S,%f")
    dt += delta
    # 防止日期溢出到下一天，这里只保留时间部分
    return dt.strftime("%H:%M:%S,%f")[:-3]


def process_subtitle_file(input_path: str, output_path: str, delta: timedelta) -> None:
    """
    读取输入字幕文件，将所有时间戳延后，然后写入输出文件
    """
    with open(input_path, "r", encoding="utf-8") as infile:
        lines = infile.readlines()

    new_lines = []
    for line in lines:
        match = TIMESTAMP_PATTERN.match(line)
        if match:
            start_ts, end_ts = match.groups()
            new_start = shift_timestamp(start_ts, delta)
            new_end = shift_timestamp(end_ts, delta)
            new_lines.append(f"{new_start} --> {new_end}\n")
        else:
            new_lines.append(line)

    with open(output_path, "w", encoding="utf-8") as outfile:
        outfile.writelines(new_lines)


def main():
    parser = argparse.ArgumentParser(description="将 .srt/.str 字幕文件中的所有时间戳整体延后指定的时间（默认30分钟）")
    parser.add_argument("input", help="输入字幕文件路径，如 example.srt")
    parser.add_argument("output", help="输出字幕文件路径，如 shifted_example.srt")
    parser.add_argument("--minutes", type=int, default=30, help="延后分钟数，默认30")
    parser.add_argument("--seconds", type=int, default=0, help="延后秒数，默认0")
    args = parser.parse_args()

    delta = timedelta(minutes=args.minutes, seconds=args.seconds)
    process_subtitle_file(args.input, args.output, delta)


if __name__ == "__main__":
    main()
