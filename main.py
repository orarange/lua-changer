import os
import re
import time
import shutil

def copy_xml_to_txt(xml_filepath, txt_filepath):
    """XMLファイルの内容をテキストファイルにコピーする。

    Args:
        xml_filepath (str): 入力XMLファイルのパス。
        txt_filepath (str): 出力テキストファイルのパス。
    """
    try:
        with open(xml_filepath, 'r', encoding='utf-8') as xml_file:
            xml_content = xml_file.read()

        temp_txt_filepath = txt_filepath  # 一時的なテキストファイル名
        with open(temp_txt_filepath, 'w', encoding='utf-8') as txt_file:
            txt_file.write(xml_content)

        print(f"XMLファイルの内容を{txt_filepath}にコピーしました。")

    except FileNotFoundError:
        print("xmlファイルが見つかりませんでした。")
    except Exception as e:
        print(f"エラーが発生しました: {e}")

def replace_lua_in_txt(txt_filepath, search_string, replace_lua_filepath, output_xml_filepath):
    """
    テキストファイルから指定された文字列を含むLuaプログラムを見つけ出し、
    指定されたLuaプログラムに置き換え、結果をXMLファイルにコピーする。

    Args:
        txt_filepath (str): 対象のテキストファイルのパス。
        search_string (str): 検索する文字列。
        replace_lua_filepath (str): 置き換えるLuaプログラムのファイルパス。
        output_xml_filepath (str): 出力XMLファイルのパス。
    """
    try:
        with open(txt_filepath, 'r', encoding='utf-8') as txt_file:
            txt_content = txt_file.read()

        with open(replace_lua_filepath, 'r', encoding='utf-8') as lua_file:
            replace_lua_content = lua_file.read()

        # 検索文字列を含むLuaプログラムを検索
        pattern = re.compile(re.escape(search_string) + r'.*?end', re.DOTALL)
        match = pattern.search(txt_content)

        if match:
            # Luaプログラムを置き換え
            new_txt_content = txt_content.replace(match.group(0), replace_lua_content)

            # テキストファイルに書き込み
            with open(txt_filepath, 'w', encoding='utf-8') as txt_file:
                txt_file.write(new_txt_content)

            print(f"{txt_filepath}内のLuaプログラムを置き換えました。")

            # output.txtをoutput.xmlにコピー
            shutil.copy(txt_filepath, output_xml_filepath)
            print(f"{txt_filepath}を{output_xml_filepath}にコピーしました。")
        else:
            print(f"{txt_filepath}内に指定されたLuaプログラムが見つかりませんでした。")

    except FileNotFoundError:
        print("ファイルが見つかりませんでした。")
    except Exception as e:
        print(f"エラーが発生しました: {e}")

if __name__ == "__main__":
    xml_file_path = "input.xml"  # 入力XMLファイルのパス
    copy_xml_to_txt(xml_file_path, "output.txt")  # 出力テキストファイルのパスは仮のもの
    #2秒待機
    time.sleep(2)

    txt_file_path = "output.txt"  # 対象のテキストファイルのパス
    search_string = "-- autochanger"  # 検索する文字列
    replace_lua_file_path = "main.lua"  # 置き換えるLuaプログラムのファイルパス
    output_xml_file_path = "final_output.xml"  # 最終的な出力XMLファイルのパス
    replace_lua_in_txt(txt_file_path, search_string, replace_lua_file_path, output_xml_file_path)