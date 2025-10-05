﻿using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace KartRider;

class MemoryModifier
{
    // 导入Windows API（内存操作所需）
    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        int dwSize,
        out int lpNumberOfBytesRead
    );

    [DllImport("kernel32.dll")]
    private static extern bool WriteProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        int nSize,
        out int lpNumberOfBytesWritten
    );

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualQueryEx(
        IntPtr hProcess,
        IntPtr lpAddress,
        out MEMORY_BASIC_INFORMATION lpBuffer,
        uint dwLength
    );

    // 内存区域信息结构体（用于枚举内存页）
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    // 进程内存操作权限（读取+写入+查询内存信息）
    private const uint PROCESS_ACCESS_FLAGS = 0x0010 | 0x0020 | 0x0008; // PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION


    public void LaunchAndModifyMemory(string kartRiderDirectory)
    {
        Process process = null;
        try
        {
            // 1. 启动目标进程
            ProcessStartInfo startInfo = new ProcessStartInfo("KartRider.exe", "TGC -region:3 -passport:aHR0cHM6Ly9naXRodWIuY29tL3lhbnlnbS9MYXVuY2hlcl9WMi9yZWxlYXNlcw==")
            {
                WorkingDirectory = Path.GetFullPath(kartRiderDirectory),
                UseShellExecute = true,
                Verb = "runas" // 请求管理员权限（内存修改可能需要）
            };

            process = Process.Start(startInfo);
            Console.WriteLine($"进程已启动，ID: {process.Id}");

            // 2. 等待进程初始化（根据实际情况调整等待时间，确保进程加载完成）
            Thread.Sleep(5000); // 等待5秒（可根据需要延长）

            // 3. 查找并修改内存 星标赛道数量50改为120
            bool success = ModifyMemory(process.Id, new byte[] { 0x83, 0xFA, 0x32 }, new byte[] { 0x83, 0xFA, 0x78 });
            if (success)
            {
                Console.WriteLine("星标赛道数量50改为120");
                // 你的其他逻辑（如启用按钮等）
                // Start_Button.Enabled = true;
                // Launcher.GetKart = true;
            }
            else
            {
                Console.WriteLine("未找到目标内存特征码，修改失败");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.WriteLine($"UAC取消或权限不足：{ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"操作失败：{ex.Message}");
        }
        finally
        {
            process?.Dispose(); // 释放进程资源（不影响目标进程运行）
        }
    }

    /// <summary>
    /// 在目标进程中查找特征码并修改
    /// </summary>
    /// <param name="processId">进程ID</param>
    /// <param name="searchBytes">要查找的字节序列</param>
    /// <param name="replaceBytes">要替换的字节序列</param>
    /// <returns>是否修改成功</returns>
    private bool ModifyMemory(int processId, byte[] searchBytes, byte[] replaceBytes)
    {
        if (searchBytes.Length != replaceBytes.Length)
            throw new ArgumentException("查找和替换的字节长度必须一致");

        IntPtr hProcess = OpenProcess(PROCESS_ACCESS_FLAGS, false, processId);
        if (hProcess == IntPtr.Zero)
            throw new Exception("无法打开进程，可能权限不足");

        try
        {
            IntPtr address = IntPtr.Zero;
            while (true)
            {
                // 枚举进程内存页
                if (VirtualQueryEx(hProcess, address, out MEMORY_BASIC_INFORMATION mbi, (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == IntPtr.Zero)
                    break;

                // 只处理可读写的私有内存页（避免系统内存或只读内存）
                if (mbi.State == 0x1000 && // MEM_COMMIT（已提交的内存）
                    (mbi.Protect == 0x04 || mbi.Protect == 0x08 || mbi.Protect == 0x10 || // PAGE_READWRITE, PAGE_WRITECOPY, PAGE_EXECUTE_READWRITE
                     mbi.Protect == 0x80 || mbi.Protect == 0x40)) // PAGE_EXECUTE_WRITECOPY, PAGE_READWRITE
                {
                    // 读取当前内存页数据
                    byte[] buffer = new byte[(int)mbi.RegionSize];
                    if (ReadProcessMemory(hProcess, mbi.BaseAddress, buffer, buffer.Length, out int bytesRead) && bytesRead > 0)
                    {
                        // 在当前页中搜索特征码
                        int index = FindBytes(buffer, searchBytes);
                        if (index != -1)
                        {
                            // 计算实际内存地址
                            IntPtr targetAddress = IntPtr.Add(mbi.BaseAddress, index);
                            Console.WriteLine($"找到特征码，地址: 0x{targetAddress:X}");

                            // 修改内存
                            if (WriteProcessMemory(hProcess, targetAddress, replaceBytes, replaceBytes.Length, out int bytesWritten) && bytesWritten == replaceBytes.Length)
                            {
                                return true;
                            }
                            else
                            {
                                throw new Exception("写入内存失败，可能没有写入权限");
                            }
                        }
                    }
                }

                // 移动到下一个内存页
                address = IntPtr.Add(mbi.BaseAddress, (int)mbi.RegionSize);
            }

            return false; // 未找到特征码
        }
        finally
        {
            CloseHandle(hProcess); // 释放进程句柄
        }
    }

    /// <summary>
    /// 在字节数组中查找目标序列
    /// </summary>
    private int FindBytes(byte[] buffer, byte[] searchBytes)
    {
        for (int i = 0; i <= buffer.Length - searchBytes.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < searchBytes.Length; j++)
            {
                if (buffer[i + j] != searchBytes[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }
}
