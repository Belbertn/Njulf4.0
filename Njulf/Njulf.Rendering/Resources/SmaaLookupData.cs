using System;
using System.IO;
using System.IO.Compression;

namespace Njulf.Rendering.Resources
{
    /// <summary>
    /// Canonical SMAA 1x lookup tables from iryoku/smaa (MIT).
    /// Source payloads: Textures/AreaTex.h and Textures/SearchTex.h.
    /// Copyright (C) 2013 Jorge Jimenez, Jose I. Echevarria, Belen Masia,
    /// Fernando Navarro, and Diego Gutierrez.
    /// </summary>
    internal static class SmaaLookupData
    {
        public const uint AreaWidth = 160;
        public const uint AreaHeight = 560;
        public const int AreaByteCount = 179200;
        public const uint SearchWidth = 64;
        public const uint SearchHeight = 16;
        public const int SearchByteCount = 1024;

        public static byte[] DecodeArea() =>
            Decode(AreaBrotliBase64, AreaByteCount, "SMAA area");

        public static byte[] DecodeSearch() =>
            Decode(SearchBrotliBase64, SearchByteCount, "SMAA search");

        private static byte[] Decode(string base64, int expectedByteCount, string label)
        {
            byte[] compressed = Convert.FromBase64String(base64);
            using var input = new MemoryStream(compressed, writable: false);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(expectedByteCount);
            brotli.CopyTo(output);
            byte[] decoded = output.ToArray();
            if (decoded.Length != expectedByteCount)
            {
                throw new InvalidDataException(
                    $"Decoded {label} lookup has {decoded.Length} bytes; expected {expectedByteCount}.");
            }

            return decoded;
        }

        private const string AreaBrotliBase64 =
            "W/+7UqO6NYMLwvQAwNlVs0YjI9g4AAnQvEo1eLNbKHerkgC9H3z8/+ckW8fzVeOqDR/QO1IzN+YIAq+tx49jAEKfWIKXa4t0cKd9BLtvCjsvhFp+fpqHd1ou" +
            "WLQhtOnvj3wZmWAnIg5H+yB4pmShpVvXOgYDASPYjQg9DHMOwSeZ0ElLNfYGrzUBjrzYHJl1cwxdhRdpf8Kvu/ODCqHdf8msOKrG5ROLnrOubAsqmhzlHgLM" +
            "wyz4SwjSTBpXJOTEV/Y/eDfrB9piUYWkEFGiBEIIeEvbPdXeEzH5+BUnij98Z++V/3tKI+hgEQ5RCEMBCcYDUurQ/r5m3oJuSut6je0aG9QuKl1dwQ/y1zx5" +
            "faby4Uzf+npbHkv8v7uFvzAONbEmGbLET2v/v8W00j2JJb8zx2d894q9t19EwBOhEmmERKSTrMOgLrFbk7YPDAkf+GwZQZIB4DBAxWHssD0Cz9PbfD337Ur6" +
            "wGwL2AjTp0xXpWgILbKLhGX1iAvfOfo4/EG3ej6+6MnuIDbAADFIQgSPwD45r9Ta351a0V2vReNFc/UNX3N+rJi2/aYVAwohIUQnyZi/EZMIWl0Rva3f/+G2" +
            "5hCirhqbJVNok7sn4JKaonnI52f/Ko3O4mu/vwGSpC0COwwhwaSH0yYkQn8REiNwAolr+zNj3eWSdA7eOBb0LPy881kHnIbWYQ5LxgF8Dhat/ee9nq6uaZrZ" +
            "vbuATb6LYlRRyApZ6AgTB/ij0zf2ywc1hCRUgCeKKN3lV9tJbtxvHE+HSmkEQmDjJPcd1qq4tLBbsmUroxPdIKjtE/4IoF0dxhygB/9/X7UqfQ8fEEBJPU2U" +
            "js5q1kprTZCRqnZO64Nss65w37vvfT78/wERAClDQBxRYjmSpSkacVokSlv4AO77EkuE7KhVTu2qx6qdV+0SgDglEFKLplhO7ceGa/OqVa/GGBO5JP2LiIfR" +
            "ngo72rDDCisPJ6xwoB7uUw57onL1UrvmDyWlllqsYID9bbYXYPz/n9Z09RShEBKhq5I41m7eJOefTKCrbU3RayZz50FXrQgzjlJK6KE0p2pR4JAo8R50GTdz" +
            "PE3GsV4i161ESKDeBvy83fl5I7iXcB6DQIAwFbpBVKiqiuaLpmfffPXMSJ6fW+kuSQlcmuC5fOWNZVlPq9NlvgqrauVu2GH4jJ6LU2D+DoGVS/W+RjrkQpgB" +
            "vFlopyj9cKpSCwyhLg0tz1cnZvNmygdYPLAAl8rhIpwGbEv3/bfSUtV/QP1LF1A8nHSi4imXwtHxo0GXM/E9KBCFxqLRWNAjmhhQykc7t2+KoccLgsulZ3o2" +
            "Ru5ofDLkZ+r/z2eO5Gm8L1QFooOWdmWhod3CerIsZJkYakQ9kKuQR1R/SPt/vsRgpVnKQVftJAuaIGLIKAbdxCjWUvIn506UjY1NGaFaDlq6Id7Nyjurpmh+" +
            "b+aeaF0ICMIZ2U4ef1pFwRUCC67cdGlL5DZ3D4ZeF6JxVRCcFpRvscbXtWNg/dJdHqFvuShw32FB+LfuxC9d9dGbVyeePcXAvwYOfP2nP7zxBbvUtVzbdVzn" +
            "ZQ61hgv7s/DpH31wa/dhXykEva89G7BRnc+5NlNVuOCGOxz+BONLbcDFnHEPCx/eLog71K5f4K/GD/4hbnnre76XE13vLbrfx/8Lny+ynu9rkYWr9Ts+o5s/" +
            "5uy1Dr1iGZnhhBIVapxhgfVt9hvsnhzPfPjURE3epMAaWgccXpDF+BX74B/Th/iQH+qAsk/wnfnVnhSC3teeDdjCc7733aTZWy1yHVoDgpjwSJQ4TihQguqY" +
            "wlc7fK8DMbuyasYl1vDyvYcOObIYFuOCBl4RX/C2QIoVx8hT+Frvp3IxD99Dswf866rYFCxZr93um3kl9+CBvOzL8MpihM7H+/JnUjQ7CpigQ0hUkj+m8j+G" +
            "RoyxQ8HWeq+63MdvJfUhL/jrourkYgjJOu22lxbiJzO8ugTyAvPxvv4FrkpMsRezR+L8oXqjlnKqiav4nue13sPkr986XfHBPMSLyob3RctqlEcmfJlleG1S" +
            "KOH9fd+/To7cpGq2hCQGwBrqF1VYP99ZSR0OBl/rPdb65nVGlNrsIj64yqKTjUwTRBobdtbhdalukxU6X0QCpMnW8E4if6j+UrRSC4im7hghe97X2lsFGmf6" +
            "9nX1nKbkobZ+E9JNpCkdVRlpveH16e4pRRrM18NwJBaEKThhp3w8Vv3ZNhIaXM9VGrznea33UO+71x2VXrr5kAfaMjJIk28keMNmHt7AKzF0ssK7bT6JhMwl" +
            "rgxS/ZVca2VsnKldPgXW877We7jGS1J6hLQlNxvEpA6orKN94KzAZYOSwlKINjrf9/OFlmTkTWiaPH5RfTpLBZ8M03eLTYL2PK/1Hr749y9pGZ7opVocMphb" +
            "eRVOShMcZ2xYinuqOC244Q+VJlvfqMKTuSYdA7IvNsJDwPNa72FyVhYvDGniZNt5XYxLMqvgSw1vFKV6CJNxngPmCwkz0/RLsdyVSswiQh3ZFtBxej2Pa72H" +
            "aj+85FlRI6bbPp572Lj9rIpykOFbcXijOM1TWErSpONMfj0abwwWwf5RXW4t5VJQIAducg0wuj2Pa72HyUU2NM/0cfL9O3TwhbEqFUlZoG+KDGQdNckN2/4M" +
            "G/ylItc/Rb1GLqDjIAZ2edmwKGgdtZ5jdsfI27bbR7YQlj4sfejUQ7klJpNkzsacMCnFi1A+GaSHKmtWni9RaSwfXYXXUHyhtEip1goUTedSIwo1YZ0yB6bX" +
            "SOmlS9iwAZIvgK1AuyFNv2f1GJYqTlZ62766Cuvdef5PcsRylVoTEDod6COYPGAisCZYeiMKNanSylnyJoEta2rzRPGg2HckOAzoIvJWcYKDw4t05y9T2qJr" +
            "vUXoiKFy9A5kCuWYNzJCuGzpXqzAm2CpjSjUgkv48aXKiQXdC2NaacUpdo496AEbGEF1SyYsrcfTQDlbAtuPzkXdHwfeZGjuGkmksflCsVSuZNqHcYQOEyyt" +
            "EYW6VPfLss7G/RHpHGuqeeLk8GKCoPtH6cgewsYpNmw6iYd0Erg/KD4jtnIDx5HpLK5ALQB7QUPoMMHSGlG4mNOTsuSMHQd+7Nf+7H9VtGpb/xpt3j2zVWkq" +
            "scX0FTtOO21FAK+DVovPibbg9+vHOaa2pjAMkULXSrB2KBpClwkWb0Ths7g3OVW8jQJzVO8+sGDFZvvrOqy72MqPXv4Df3fMP8m/1ev5LixORRs+omcUVHQM" +
            "m7bYCljQ4nOyLfjZfcRIrLIHQVHoKpjvFRW6TLB4IwqfZQImsh7bGcyYmRWotNF2dKMfo0xPTvqnJ7uPXotX15mdf+BXzJccukNic+EjRKypZlqMUmpVLT6F" +
            "4OcOgKTxTSwdQAFzvldY6DPBYo0ofBaZq1gm2hJUTvgzYmIZCpRaP1gg8I/KIOPHy17/yDSNncXJPvAj+Rafcqhu0iI2B4HPikaqWnzcIOnG4V5ssdIauPme" +
            "W+g2QfdQFF73ZNGi0SyRlsC203V/howttQw5Cq0W+A1pRvt5q70Ys61rN6iNX+kH+knezRYfUiiuU/e8ILNVtfgUgxsHgHkYCeSq+Z5b6DZB91AUzjV8hD8l" +
            "K+OZNDS+JeJ4Y+6NLOFY6XV/VTCo8xMxOO9dyOfsSG/kA30n0XSxJXR7XjTqqlp8isONBEANOlc833MJL0xQDUXh0xs9xhegERlYJhWNMSo7x0wylofnyX4T" +
            "d/Jr3jYVnPdFpye58ZO1e6j7u5jikleLqhafUuDiACC6AWu+p4QXJqiGorDm+DG/Gig41yyOOKrHsHHmxnZjMIPnlH5LQKXWeeVzHgoPRWcGuWQ7v5B26Vcy" +
            "y7Kwqlp8SoNPJkBPpcz3pPDCBFUjCr+y8c/y7HxFRqTxyDKuqCewUeF0YkwSXvf3xNCD2fN6iEm3BUDRml5NYuUHu5B7u+fFmKpafEqFCwbo0nwPhDcmaDai" +
            "cO7Jz/YMHtBxriKkiY54e0usTkS5hj84B5x8EXlLNzmYsKSZnI9y4Rs/KZ+Xz4QLLT6YdTKdznzveHZMcFodMdfkb38MrxpKMqw0GzFYnJWIyI37pyY/ulSb" +
            "coI918kX2ZumACgbE/JBzmLle3ohF3JX1eLzf68JYsXPuHp1PMZX6yt0pEXaiCFT5Paj8ZNrdU0Ntom4sT2bVG+asqrKxnR6kKNceeZaVLX4rGbXSsCx07Pz" +
            "HotIbLGTAkcLzCrYwrhP7Wc3w4fpQpF7rgtfxZiHsH2tGZSROSvXVuB9mhwMno/5GTJLo40EdlLmiATDrBKJLPdntxLBohR0r+JODr7IIe/CxqxWn/MyibWv" +
            "xaepQa3Fq8tjefau8VUkJE9ptluhl4Q/q2/GiqkEG2cf2/iciG5ZWGgNM7IsQ6HFp+nBwetjeLbeRyI3MLZvkOgzMhf8RUMzVcd0FwqsZdzJyVcxyYGdtuaz" +
            "1mlA+zQ7oNfB8dm5ejsPkcgNrNCIjX7R1MzUhUkpmFrH2brz9STZhy1L29fi0+TgKref7TE9g2s8YEGRpDRbykIE8HKF+oBUgitqLwlhgQ17lwp4VlyDWnya" +
            "HozZH/Ozd5mZkZgOdvNdmfD1/KdNoxohEDUXki6CrCs5+JpNhxczbJl8H+/T/IAc3Y8hMxkJ24DCiCG6tG79j8IkyED5rqd9O5fvxYaDy55VDJxyXl2qeJ+m" +
            "BhmF92Ydn0B9svZd1d6ldTX8BAjSEABu4YbOBizlTnskmckViMTSN+atoi5gzlCG+lTeorUIBSW0amqjWtA8Tu0eBX22sXdfyXGAyXM4H7XT0B994GD7tfEX" +
            "88fX+HYMAWXWvCXLVpbN5fEFuie5IKk8UNsMW+G8JZpbqjbcrBVqmppuRSSLhbFtwUO2zEqVspyFPndOeQj91Stvyf5GW06eCnRAtdUdvkQSSyrnXKu8UHcJ" +
            "r2/IA2prJZhEUxvNfegH1mKU3gyZWIryfPePVnoDo8KjJ3QBz3QW6oxp+IzPmODr984kmwoTlmJ1aKD+CAXcTSayGmNqm9gynsSE1Ww2unc/VJk3O6HQ3KNp" +
            "dNQT+pyff8b22WisrcXANnxNCLa9g3WeEhXTYweBlLqP2pBo38abEU+iw2o2G9XaWWJClklP0GbHDibkOTLvW61wavjQ5/ziUzXnOYvaOa8Cn+mBbwmZIoIO" +
            "1oXzHaRm1APUBsvKtji9cNHcuhyXClMxrUbwrvCtNUOeQuXZqUeq9LITa1Lrog3Kc3jeZVO6kROlJSQIJjjnST0IgQtVidoWth1Om1ua6BOoMZEZE9AU43LS" +
            "IxDCU5OXtDwIfUavaDrka54bD7kvCtCdHPjCdnJBd0a4wJGmqYeof7WYsDFmipLAM//u8RguIaQQ6T4xHWLNTvOYZdbqIm7GAuGPlCiPhdZ7VTOAHGsedRZB" +
            "gzVMK/ts4is5jDIx+2EqSs54p8DYGMwYJYF3zFZz05h2IKQQ684NImy5EhNk4oVWPNa1tl1pL6wlJNMMfS1fDV/TiKFPpuZxpJPsjM3UqF6MfGEbOaVJhvkI" +
            "tYEqZT26IeaNCZQEwm5fV1UX7R1CCrHuaBBjIOA2foBIaaZ1xIGf3uzrCr1aphb5JHhq44NsbYY8yIHPfKX7SZFrgNqIIO0LM+EDJYFyty+dsujuEFKIdX8W" +
            "IMWeai7VBBWzOoJpxcP0hM7ntY1SsUkBeh4nW7JeNUbLsiJdRIx2+sDTrw8qjO0z3E4JlASq3T4PgJBCrDsZpDBiQ5W2AU97Z1r7zEA53oR6Qqu8rlENF6qz" +
            "dTmH7XwSfb5GFdGtvJ02S0AZvLUQQ2MCI4Gu3T7tQEgh1q0CMpiwI5CijY8aROKF1QWauOXJ0Kf3+kYjPgzyQfY8LmTlowwGHyTLD8om/vqktjUVLVQJlAS6" +
            "MgI46HfHgBxmDA/bsW3AA0uzZsxE8kdx9YTe8wZom+EMalOteI5mCx9kp/vMlK1DQkyMi9pW87ixJwglgQUyAjhodiE0s2D/tnaIayOWLSmF6nSF2t0MqtUU" +
            "T6/9cvJExSAT9njhg2QXqI3QU/sQ6zWBksCCGQEc9Lo1wARWHLAl30ZaxzqDhwkTQsGwkDOofxE6hQC1pM2eNoOls0ZMD1Ah+JeJtdCIksAiGQEcdLoVtgWs" +
            "OGJH9ScvLyfbjMipuep0hR49zqDgcvJQfqQ6Xj70alLbirONlXSUBBbNCOCgz73GtvAbTBioYZCiDfFFuer0vOkZ3ghTIx8+LJrlkrqcbMlKsp0h7z/Ivb6o" +
            "bblQgARKAkvICOCgy73Gdup3mLCnloDLNuCBieMNNT1NwgyqWimBItPIB+hFuMdYWeB/4aWOj5tqr3ZOAipIAl+UATC4uwzlEesUZ6PTezytZdeHu/Ts21qt" +
            "BfPLRXrQSW5Ep5yIdUzn2feKnRYMLx/xo5NQF6kFw/f/NJy2olhLkLfgKFkQU5QkmCdKFkJFzznRKam0Jchb8CCb5UkYVZYoiClKFsSUJQqhipKEUWUJwqiy" +
            "BJmJz68JAAyDxsgluMR8WnTSIxZ3tQBwcFtaODqtlQ8rMAYvQTqfFp14xLqIhnx19abV7qF7Z31zT25sRM3fNj/rs4iFqXcfYbx1Ee85d12QxV76x3zFjrKi" +
            "TgUTBDBQEH80CLDSy2x+em+75sOMWHL41npQD6AQ++cvM24ti5EQIULERdAf4YDNUV5vOdO8fWS8v5Lz/G3PwLlxN02qzWVNW9jt0JNbxPA6SwtB94ZEGz2E" +
            "0XZxq9v8cZzz/SWhSufxl2TW6208q0k2OnPqoRLhyyAD35oB3FYybSDf7l+y5cz0p/4PI5zT+Xp2rjq2W7BC49BpZYVnkWRUQZu2WRdZXkeAYkE1X8Sqdn3M" +
            "x3Ku52b2tNiDnC3OzPNvyitwpnux0tPS17rlPR3xPNFgLKpWIuxw+A9U6ld72/kpyiXN18X2p/xP20Xued3OV5Jz+enzUG/bdb8EE+q4DWXMNLPME38d9ARR" +
            "wqlgaprFola9ztuyJ7Ierdu5B8zdf5vefF3c6xoObDut5FKPc1vqWra8pT3u4QhnmBnH28rCoG4VL8+/4fpOBl7Jr+FfzmkNm5Ie+zhfjs6nc34s99W2Wber" +
            "btmPsKOGK3m9dy5IMB3YODHJ1aTlIWrO8vBKXdS+7dvxd8nd55X2PJzJcdzxTdGnJt27c67yKdriFvbh4iZfG9GrRYnh5fkPcoh3OabP4V/3AmJDpiz3Zdv7" +
            "+VJ0Ppmz0wKjNvX6tXqBdyQEGA14dBhnE5eT/UpqpbH5xVfM/Xfx/eJGc7k/D2d8HPZ0i2FikpMa06KrVNxDuzkvLz3MJUwiBPzz7pM+2a6RK6bz+ztczowP" +
            "77KKHY/32qlPKpekqATDWcZRnoy5GwAEiSHCGpyCytKyr5X/nuU68HbSe2WUuIfbmJ0igCcGh4s1hbzfmX0aVtdAX1obtuxR8eT/gXEpoBSE9VVlpye8zmvN" +
            "mxTAIc0b1spYCBEieNQAS3OBgHA0OYcUKnJutDJU/qzCwm/sTh+0b8DzZjoVMSkoneGVJmYqwdqDukW6Lki9f+LdyVjEsnltpuoszdRjL9GjTOIzqSt9D3oI" +
            "EMR9FRmk5An3Cn9PpYyN7+1kZG0gh4WFb5S84MBY019Ka6WU55yIkIZqcwVVlWb0J96PqPcvoicdAS2EltxIDSQnGNeQyZvXZh4JO5dOnJw+kegAGmIdQYkB" +
            "9c4H9o3xX9pqc7mQqY+DidNf5OEWuhyEPYyuFzHj3P8mww2lbHI8yjTIuvJOTzTedR5LlZhtMujAIDTGQ5gEv5UqkerI+sGOZsOqeJFRxPLaS5VCzDUkY6DE" +
            "4y/SlgZCgjIDbIeJHur/h4pyqxAwiIUJPNMq6g1pQrnbP1MCCCqoBgMa/g9Ui1zFTTqnkaRHeoU1PkEuE0iH6S9NU6mARSOiIMIAs+WVW0qD2CcYu2WIhKUF" +
            "KmAlTlITcIiSJc0/X1knJxxv1Eihue0YLtOhCDDK5bg5MYH1+D8byJADIlnkMvEcuzCTi9r8dFHxSD1aRBtN5GyMEgvh8P2RlURgqf9l4KGGcj6TFMKGM9Oq" +
            "rLdYpiSAhYdgStoRSJP34IYXMtFhm6lo8GptEIxwuw6OLE0kfG4GOhmLIDxgNo/s8DVB/fOSZGFPqA+XXJMAN9HBR9OJ8ZhCle3kiNGbUliAJYkgsSRNILyH" +
            "F+Uk41WztXnetUOOkT4RA3N4HJjpiTuScM7B+NKzzaXAsL5FRJrrafFZlGQR7QJ3mJFHNOXFSXCEYDf1/QMa1nSm8474Hg4uQFtTr7JFuCpTWqgtq7arLY/a" +
            "c+dhR3oChVXGN+1DgcL8gYvwjb7//sDgjszMoBbVhf+PTUZ7MtQWcxiaIcAgi2TVWVA0ToWAPS0rP0dGKKS2eXgR7ACcZQtAjqvGJqOZrPhG12Klra49gSln" +
            "+en5ai4yRQ88RcU1H186zgaXeJLBcClYk+qQQ8HuhHO7Mj6btmH3OlRYuixIEraGEyPQgsYQJxxcJIVlQbkAQDtDMBqZwuSKb/Qd23iOyFhgyiWs2/GU7obd" +
            "iCeSrTox48ziWdhxNChwPvh3UrkiNr+vq+5CeLiZvkLEZZIT+5cssgNoDwBgHlzcGP9HJLIlMRHnSgTuoOwIZgL3rhX6TP6CzPE5Az0v6FJcoHAJ55recwW+" +
            "tJ0zOnvVTqJLiuovLN/p6DtD97+UaEyuUCyVmRD9/1iVgLDIW8Zp6Qs7x16DcKBaNE7ThUZ7RFJFRAp5S5JOHV4grPv7XM7ZRCHPySP65B11vY4XmLLhrci5" +
            "icvo2a5Mqx5tcATGDOa1JNx1HQYCABTZKM67reIbX/ZDj3r4QoIuOtZJoqvUTRb27+PnIn9HYi87862cn+opqyl/H69ZTRs60oH/Ag+j/4AN1Ha5rLmrZkOz" +
            "w4sgwaQNoTcIwD2WE6QzPrW1Z2yCRQUG/ld61MH5Us9l7EDUNxaSgRGLK3g1oDvH6XDxOkBUK8nAkHk7TGgAGCAAu4WqSPn1+T9sfkMq4kB3+PZ++6GLTHwk" +
            "e7qmSzbnOleFjBbV+f4haExYV+ymR7bmc6FKUU5Pnk0Vq2hLPdJhioByC8xEfcOJH+QLTbCoQALk27CCd4+O73hO1Xa11r5zOAx8I0OkHaSOgIrgka1aCNQ7" +
            "rgaGR0QghgjASnXrCvynJv9vB+GklIAZYhIF7+kMb//pvvtTTVV0Sc+YKTvhJrzq5V8LFQQg6rMDvsuVfNEdQvbscJhEjvhKsa/TjB2cyLT2pnJMsLBAgfPX" +
            "vBdJ2YG6QyfUZOppaAy2dhmAC1TUWDAdYzpWDwOtDpsnUeqPJ2I7PMQkhqzIqM3wT03+5oIBcxrPteBJCen5npzRHM3hc/B2Rj0Wc0WrfhlvgA0ROGYY0iHr" +
            "MAu25GZ8wU+ExDNiJAY+NQDduDaNlUilG5mgW6BNa0NOeUqQm3RiRUVUUnWdCWc2P3qUEypYR2xEhBMBoP3xRR5GXuB4tmvZpm1a7bpZBG78D48iEsR5Ilgu" +
            "492Y4IYa2J8m/end7qva+I3woYIDhjnGdERdtOYNToGJUIzMKD5BPFnfBN0Cb1rUXuIB3sB1OqliIp6lhGa62Ygg1wfaJZrrSFlUDATHp94kSoJx2JjzhFtj" +
            "5vNM/0bosPFgnoQkLvSQgiOduBAt4LycwRS5b0YEEwAMDptgQIfMzftMzVbd7IwuhM8FAPzkFCYs0QSV0GJalUVVDlLPVRqHsIHIRVNt/ZWxFhOlxMWZZcEZ" +
            "h4aH86lfJOk48kPPdzznBOyYpckhA0y/1pWIhgEpfXpo0IQagCaQECpYczSp1krgQIUACiiPHlGfeuyACJOYUyl0sQDOUAarCSqBN63cjA5zRMn3kmMauXCB" +
            "TKiYQ7TNs+DvBwy0qU7+UnghksVgmmZRHI7DPoDF2f/hZ+yAS0bVLn/Bt4rZMCCHGCx9WBITItXKb0EKDwYkcCBpXWCawlHbd88UAB6p0ExQCrxpNS44OFU+" +
            "HuSekN6L91Q0h8o6SlVERZrkshOBkcVwluVxMo6COSIAk9eYvFkJdPhIk4gEculCFA8mNJdCmM4ykWbLEcCCChEsboEwxtA6NNhBTJV12pJNEATeZBbSFZCh" +
            "TpgoIzsCqMK60I25gYxNhDjlkeDGqVEI1aNZPkniZHmV70uWaUqo/38KhJGcR8PDGVtRiuNjlxqoR/Vn9lYUiODAgAwBDAgS1NVjBIQghAm+1qdLnetLGBg1" +
            "ERnmyF0+AC1vYaTgU1HqDlkEGLAcUk6KNIuTcVgUMB0LVObs1tOguNcRsym4Go3BNKG5aX6YoAQIrFcihgcTKgB40A0gkK82ZBoDiCnp81YQoh0YanEmbqSD" +
            "t9CbrEd7XHZpHONwkwBVEn2MsPpIRz0uiyLL4lRnXxh6gcTd7soqhlY/y/KkPNeja2JgyFSyess0y4aoYxMEsKHrHGJBAXfnzDDFixlteRk1mafYsU4a5Jn1" +
            "tmvbgFa/FzP+p+C9KgENJ6/3+XJhFWMrpAjh6qTS1Ay+1Q3oiAbmxTSfdF0ST+ETDMDIVv9gwlRP1kqPwSOhRGs7qXdZrKBvG5shggdLpwPQhpRY3YC/bzKf" +
            "ziZFd5KC0z6QgFY/K0GdRp5vIebSSTj0FFI0FaNr5IjhzxfQyfdcDrQshVbBsaVFLIXGJJoiIFlv2eo7zDv0xl2ZgEUlYhEQGI1GC83ouDoNxWEXi4OxPedm" +
            "F7WrmjUjyUydQnZrPgBa/ZGYqb6ciR6DQsAQgstXghZDITzY1CtDnI4uNDvQEpqxM9FJwoJgQCtNOTp3OgAzXsKj4LLlai1tsimivBOmTnCUUa7JRSVTMo9P" +
            "8iZJJIqFCwbEkTTRRUOA4qmJns6xJlUTBwPShgMlMsRQACdkDbTp2U1lMS2snHCqQLyERwM6IvSls0ptqYDDIC9lDUwzSRppQoMCqSHO6BG8qdFCFsz9TmWy" +
            "uTy+4G0IRaH430ZC+trP/NDaH3Qmh8t/JsHKDJ+w8R/7B7DulyEHoWRT80v9lCfydEyt/Ywjkqm0KEMWm0PlcaMutGe8ZH1JpG/hB7/gSRRa4sYz8UBR61+9" +
            "NfETgfoqwMJYFZu/iqbmr+fNKsEDKTwtfRMLGz/6BUco2VIeRNYS9dl17e6zdD1iiHQMzaz84CcsngA8E9jhf2enzOgHNnwcsXyqK2uOuKn563l9pH8Xfm6S" +
            "zpfqGBibW9nEMIX1RP1yOo9/+kNapQcktkiuZ2RmaR1gBVf7z+qrrMoNzW2ig5U2oe9g4Kbml/r8Qii9rPzMIInJl8h1RwujkK5MP0Zd1tn1/YdgrsoFnsYV" +
            "y39XPVMLBRgj1l+swae5A1NLP/h55xoiWBm3qfmvTK1fHVDQ/qKs/RQrgcrii7V09EbBqyXq9XZsz5A7IZq3GucYMpMnlmnrGRa755YZnH/cNXoTxW4Cxfaf" +
            "nWqApuav5/IEF6C2O2Hjp2iTJDqHLyoj29UfrZ6JXObOBLtMwjWLVuMMRaSxC7sIABh3LzyaFLO7Ell5CnRuXILnpuaXf3EOaw5a4VXG1k9RBglkBmVThSo9" +
            "E7nMvZTYFhw8rJVonSHxlN8UwMnV16stPlyqltmnUu0bH9WeY/xosAcPuPVd7dMrTAWusxkYP4x9Zf4V68d6uqdaDgGh/I3+su98kSkxDOZebW3WLw3NoKyi" +
            "2pcv545iUm3tUIVsLYlzUJPKZCdsSj0TucT9mFr2bki5bO5KLZ3CcYEDFleAJ64t3r+wa0Zvik55YSFzhR4AxBx4wpWXb82FH1GWMP6O427zFevr7wCmmAzx" +
            "ovyNfhmc6wFNEhz9wPjKO/xg9+bXmLlGf32Xb2aBkAK00u1YJVs/QWDtJZKzdNxy9UzkhP2IrtBLIR7QLVi5bIYhpMnX8CbWFu8fmJQOuQtLWfnqO1euMgCA" +
            "M37xXZCFKQB7njX6iTW1OhAU+gY8qvFl1nsANeTMhFD+Yn3MN15wvyg4q9U4+8rLPKQLLZzepVna0qW7acY0y+TGPH6nKrmUzWpjmlmF4fRM5CEHIVNi+567" +
            "oJBgWrgKKycwNJ4EBGIehyxdHDehGsaw+lQyy9TsUW8IpF9Ok5ceETnBUjlC6e7wKwsUzFnkOtcsK08obGEDo+hifQXmDR9SBitm1DVYebbG4+l1RDsAyv/R" +
            "GLt4P+kRSwkRamXvoLKsuIyTcBRgklhGecyopKMOO8U3eyIsOKhmb6J0KdIfQJosEHXW2y51lZ7Z+/SQjoxUmh2+UYN7joDY2iNCNjDJjYC5CL/UucYWLxxy" +
            "xCksf1Mof7G+T7NrWzNYYcbTvczKE5PdePLOr2OP/JXtuPgwHmBPKo7kxhh3h5XL/ufl7B3VJE/kcgKuIJblKJh0ABjmrdhQNMMSflW8uee3WeyUluUck7J5" +
            "tWy/HHmudj9tRy5dYDStDSxMjAq+RJ3FkJW29wFuPqJ8768lneBjaASXPZeQGnDLysMspVOUJj3607v4NJrmETcMB1Br+3gdq5CNSux5ChInsuKhz+dkjR4a" +
            "StzDZZjmr1RF+wiM/wsoch594cSq0bV55rhl6bQ9cLhq4ohhzP1+AlDr+S2f2EK3oDN1B8+RW7uPIXaCH6gRlGNrDBnct/IAVaR1/l4N6nqpC89G8yyjlpa8" +
            "TjEdB4qsudqbDENiQJMnsuKhx2dUhe72RAgDIjSzSWSyFOmhcFhndyRM6Crdxpdqu2XpeGvAEiRjGzCBJdHYqovfcurI+d7udXIXSApfaeOiE1w8CNSAB7zA" +
            "yr1E7tgqX6AILjwfrlCHZCLLJai1NZ7flamUhTlzdoBiIR1HnpDSJb7eIy1IBOsSY90VydVoheGsZHlIHbc+7/6ByeBsaCnke+8i/tnxXHNXCBLH1lIlYxNr" +
            "rOejN9/UFoxnilSd4MLBOrVSRINkLhl36G4c4zl/d4+hLrSH27SHA2FoIZLc6Nc5XkeVm7OksKNdMaFnxCrvCUJJhwDVB3krBoC8iBUwdaZ80j69NeCFMEwT" +
            "OwtMJ/231HQLnZLodGIrd1BkTie4cPBSlZSJ1Rw8Z/Yu9KHG1TOtXnuwT8ZFwvK2mU7HdN2JZENrbbZSzEyJuu73RDARLgBj/ZUoNHAgUOlg3qAzMgPu6Fj2" +
            "1oHPRhuK1a466b/FN0gFV52j5DTGFo4wfCe4cLBOzqVAPZuRlOdygySxRHqdwTGeFgl1lGY4IEVn/3g7mI69VTakyTZypIidEIu63SPNE4OtVPsuC1cKBjmt" +
            "eZuFoU5p0iOxoTODQrGjkTKh0jSszSQcA0wDq7LiMyc0d7DoBKsgoG4hdI5xqpyxZAxdwwlHcl3i6DSa5x3sSaOTKsR57x11RvpsTW5CVdjmDBoSQncDqLDa" +
            "sqJoqTUpcMm8JaSTi8VqW6L/sxmCy1ywUitlAiMSItWetyBbjcDMthOsgnJsP6oxkwQ1dC5x5FauYDjCni8TkfOSVaxmDTXUmvIlWbbBfFZ4g/d0J9742Jd5" +
            "02/ytLwLytezD/qq/DXfIkSGEhWaQ1t13/l4XWzewi3I4lz8SBafhhNjlj62wqWMfKr7nvIVIVg9qPzIdUPXAK0VWWk8Ey4MAV+AZDjXBDpSqUxFLooXKbld" +
            "SQPzPruW/dGtrKw1dD1p6cIDx932No8+EfUy5EI/1wvQUGHDR4QEKXIUKA+tjJo03ps+BAAW+uuck5ANrOiUeK/s8fRCcBzjAucyviEBCgi7c4Gim48md9Sk" +
            "rEXLO84f7W6dqJdJZSasMHfJ/4biWp6TJUO1SE4wuQi9eo49cheSHSV1DQIiVJhw4CHAiWllAIA9N3WH9MapgKmPC55qlTkWT0Ow68pf0aSitB2dcyOhbHrC" +
            "VnZEryYj2g7zrG8DYyzWrb3/Inwh/TJRdmbGdNV01ViRmH9sWxaWk13ImA0g4OAAoMKAxXhIKf4wxbgdDqjwi3PVxm34IBduBL2S9WQGO8wrNBiKys6/rylR" +
            "68FAIcnpjd/dp62ayaG3hM0uRlxF7zEDBgYCJBkwYcNtp69+MEW5HQ6o8IvCxEN3dsE2eubdLXe666JFXLdm/0P/QqsxjaXekYOV2zuSrnS5FPJTZo0h5nck" +
            "BQ4i5BIdlCr6wRTldjigwi8KE/c96IqcyJU1Ig1EiyQDBo4vpsLnKh3xAzHZiX5ovSfO6X3etQypNFbKLvtCzlb0DuN7OAoSLAQAoYZjix9MUW6HAyr84jDL" +
            "YHKHz/hOrZwSlfmqaK53nPmRNhGCnTFrtyTCHN5YmAhKZ79c8cSUK5kVfcBEehjwDUtAfjBFuR0OqPCLwoy7wfgxv6IncqMXqXCL1Ct3JSNaanl1nI9kyY8U" +
            "WaOVde0oTeL4onXIrkwZY6HSPfLh5weX1p7VY1ty5Bs65wdTlNvhgAq/OIwa6pJV1/k+444cCzAYUeRHshypzYGlqSGjO3KpOE22QSY9GIdSZGuMA18f+ohM" +
            "bEhmgC9MUW6HAyr84jDDSNekq5j42rXDngJ5kGrlgKzH+GqEH0jDLUpntkzL3XoPJVJO2ZKckY8+h1iaNvTH0jrEOPXDhHI7HFDhF4mMbDHvQ6aJbeiWHLYt" +
            "Uiu3/mOsRSViXgtZc9115TKTfvBe88lzne6xje7wy4MeJs5634AVcUqU2+GACr8obN6OeQuHqiYmvqKb0dAR6k3PS2Xk+mwaCdfmvUszY3Va+wbnPGT6v0+l" +
            "W2yiK/oU4kNUih8E5XY4oMIvFmP3sGRdRZty23wTp3VD59jQoJZOJmCQ1syODMQoSDLBg8e4YoW8w47YRXdYoI3hkeVBroj9hHI7HDDhF4sM7daMXaq3CbLt" +
            "D3LfUGQdxrpBpywF6tLMemjG00Jkc7rTAnr1EUkmSNMXE8rtcMCEXyT2T+jQvKlmrmMbd90AGPu/kMuUBN8mYKoJJz54gitaTrlm5xnRYtz0xYhyOxww4VcL" +
            "IO+dcmwzMu6E0lmqNKUW5B1n2+Qr64b6Vj5RVI+OWvdYG13RR/iFbvqCKLfDARN+0Vh7ilw3cx1bT7WvC6Bej05BtpnYFFgssm6iPbbyXELeoJNE4GA6ZtOj" +
            "6IoYTVFuhwMm/CKRnuJshmZkAiTkqxwY7IAzaeTYx9gptQnVdtcVY3R4MnvOO7/Nc/mh9J5It0MBF37x6NrDMZ5QZZtq2w6uK8aRPodJY6OAA5C5osnDCpHp" +
            "dE0OWp9KKWweCz738Lwr4v11Qaq886O57+EUy0d9yJH4wljM51e/sgBNN3Fqs9jVbW4/twV/ahJS90dBer3rc7OnNc56V/7Vvowbnuce+K6In6+JP/n1edj5" +
            "Ke1cFvXA/LrBrR7M5wux55rFytp1t3VpQIoMOAiQXnn95jn7xZdHVGkQqnmhFXc84JBzrTfrnKXO08ph7IEPRXxv+vwGNA2KzWKVm95P+lAadP81BPeo2k9V" +
            "DedNMuXtWercxj4AQ4p1kFEoM+aU3nzNcDNMrE1ZJh1mW3TVTTbe33fSQWfEoPdjD3wo4uNNkLleR2DAbBBUci1medTHhm3e1QTmf52AtwFrx3HQsifzIdqk" +
            "q3wqe9Wa0uYu9REGZFiTe89L7Qz3ZD1OvE9NFpV1muMcFltt0+0Sezz2wIciPn9AwmEVo0fCBk6F3SO0nORkbn38t7EfgeqUEjincSddKXs6nUS7ZJOti2U5" +
            "Vr1ur/qqXYkfFtebC/0MD2Q7rumSj1MXVRZV9F/Lh7//j9ewhbEHPhTx3lSdVGHRZVdlMGIybZ9lYLrj4QLlLBzyqG0tRzadhod4l26nb1VOzzRu0eP8VnXl" +
            "db6f4RHvhs08ME2jaG2pWiZfjz3woYj3pv09hPikCraE2bSgSk3yyi44S8O/wLTo6PJKzkxhu8e86gwHxHNW0GmgKUOySkXOiUZWZkhziNrJA0MMgSV2C9fP" +
            "Yw98KOK9KRmKOrtt+87E5lEV5qUFFlIR/gPNOZ+EnKoFC8Xtb3uMxwxKJDBeaT+rZRJaDOlZHiREmJVBCdhoAY4sfDz2wIci3t9XO/+kim7HKmvO7twohf/C" +
            "cStlEqLTZymVVrDdIwRMplqkOnBOOJABKCnM12L/nq5LSzDvxx7ehyLem6qTKswsl5l3cE86hjUi9PFgn4qLQsXMKmzvuGNdXaoMNiLX0b7asTidGggXWBV8" +
            "PPbAhyLemxpXZJ0qmwemEF9lG9aILA0zkLBI6CJ5pmSV+W6zRp0xxyp/rSh0cqwAnBE/rfY2LUw2y67UMm2SPR57eB+KeG8q6wS3FcsIhHKGVEjjdzuG64Q4" +
            "BIHqLZQuky9EVaEFNY5UstwzIns79vA+FPF+p01VJ1WAJW7YBR+HgQh9oPFmGR+7PWX3ZBUtk90sL+WYJuUdWblrBuAuZhXvxx7ehyLemxbeSlFZZsBYHQ7U" +
            "IG7INTSx0XAk67yjcyUdtA8iYCm+5Y5jhhZydsSy5p7xfuzhfSjivam7zsKZBNthIELHNKJqNyKhK8u7Rn9LVok8tDDHKm5AvVckM/bAu4L+Mi26leLO3T4M" +
            "RHoJsxU3x3xknwMoQL3tX7GrUGMa2FvAgTPLCkVxMIJ7kfLIkY9w8SNvfS/RZ6oe2qaqgwJUhqiwD5lD7UunXedsmiYuDgnTviqL352qDi0A8kUaKA7KojgY" +
            "xb0ouVCh1bwLFz/y5Q9Er+kDj06qUEdv2fABpLwVNyeNG2xljcgOm7KtNzZySss0b1EnODDMkAJ/7k2L52AU96LkQgN0AS6+0Cs4C4ied3S4rRRZNg4DEcrJ" +
            "FbdoyE1LDGON4EOaHSILLQwvZFLZTCgORnEvRuA7xY8NImhrF9RSkxbT8JUSt1KAgdRAhA6MhKit+GzX2Uw0p7GsU41V8oDiYBT3YkR2xY9Fp6OiOTwzwOfc" +
            "+q7/9QfaOtathNMN3hWwFoKdrnWxgFEcjONefBP3w+M2LnCxaA7PDGjV89f+fpU/9cxsVlipdSwpSh5t+8a8K00rWtOPLhYAwcEo7kWI6oo/IWgKGyrNHwkC" +
            "+Cx3z0L1/Tj9bYJgxRLIyXUsYDCPeukf3lVZdNNHLSou1rR8DkZxLxJmV/xx6xW/o6o5LEUbPj2m3LLJB0meFMhuw+RMJnmXG8KCvfAT/UjwrjSuaH2IKtRq" +
            "F2taDgejuBclqiv+vGA+d3+gqjk8PWBJ7Qe8guivzYt4WlkC6cuSfJOk1rE+Vds18lCY5l2VhTa90ML0gIulbDEcjOJehLi74s8JTWlyIprfHJ4OcOUunHlh" +
            "1UkveR4g+Uam7pLR8Wby8diZYXdSdM+8q7KsTT9VlZw6i12saWkcjOJeJFRX/DzhVjXOTkLzm8PTgS6nNx9orbRekdeSZ7GQ7+ok73Yj3x6xfTz2JsRlEaHB" +
            "uyrLzPTxaMSmGtjtYk1L4WAU9+Lg6opHwoLUuK7K7ubw1KDL7UMz4URm1uEutOJpNJ7kuzrJKwY775t5PyO3fzB5t8y7Kk/f9IPDL2oXi4vhYBT3IqRwVzwL" +
            "NeUeNiuj7ubw1IAGg8mKBk6qrVPGXZYdpQCCV7MlLP0Fmcr3Y3qJ3ogKGa55V+Vpm36TogenzshmF4uD4WAU9yKkcFdcE+6K5BFOVkYLNYenAnx5w/GOZFoD" +
            "ZjTeSbvSmqeROMjJd3WCcn8hHURMhV02Qj50BOZdlSU1dbqLRetfxWEFDAejuBclhbvin2tY0JImz1sFjTcXSs91NDoWPWl5ocKugp1VlWuelIjFE/zG3jbL" +
            "vbgHIVvi+z2XdwVGj+lFRW63oZu4WEC++UQQ3xXfF25qkhrnsoWbw5NB646O5/mAe1YBMhpn4hr1EI8Bl3l3I515fyFwU2JzJsLIhQrbvKtyOaJcrH8tdpNN" +
            "Ezv71MrPl9mERdqIXIVVeyfpXGWXqAG8oyuphwFfkEv02HB6V/9KeFYeY64BP+bCSdXGxXoNvLqt5Ol+ukIzkknLS4mMxp7gpF2oUcfttp1WXVHqH92QcHJx" +
            "K1YmGdLFMvo1YpVbyPxu0xXuCM8okautVbsT7aw02kEWha33u37zhIyucO/qX+UaeHVMkQGde2BdrK0bqKml+c4/3SfbvMcDZQQhkd7Y1ztxh5bGhkobvuv7" +
            "a5EuifWMeiGCbEgw4Jp3VSYWzsvFEg9tHpkLC5k0Mfzz84/38T4b0TSRk4CprVk7hemYVzYjMKuuDCa0AwHkTtom6WKFOowrNblU/TE6ZhOSCcdokescbV13" +
            "wp1SOOG70p2sdxVLhFC0s7JgM6zECt+hbNWr2lCuAZLq4yGMaqOXWDsy1P1j7mOx7Hmytyy9phrv/ifeKbhGCCd/ZMOqK1VWz2S6TrASK1SUkBWaGhp8OPhR" +
            "afUKWVv5WZ07UPvI3nU3AchNt9C9ZXHhXYURugBpS2Me4tDLPbSLu1iApoaHj87RhveU3Xk6ztUP9o0f20V90md93pf98xgVrcPN0PsSgeyCzur8zheEEG5X" +
            "s+DswOSSAbIvD6TgydBn/jXfxFR2rjawnWuoUEZTPCU8ZSnNaD42UgwLff8DxOHbaElkOmUsp8VYDiV5nrUVXl+jptYGZ3cC00PRCXCnJ/ip/ivWgbRgVYyf" +
            "IAiwoZ88Yn0s4RzqSMUyke9/iFwkYYosoiNekzlVmcimnL/NBStoScbIc2hq7b7lkx45oXi4Fz0EXKkRpqB/pprEA6sBmbwzQSUBBTsSeHKPV+3PUIEwUjCD" +
            "yQ3/c0d7vCZLqlOVyUzkU14yYatZcDY4by9cHl7G/1x1L3p7AE7TUCXov+7GlBIOR01olCg4G3SQtYZCjOMvUYdihJCKeUyTdeibPGPpFpulGfBqFpyNrrqr" +
            "UGySLbgv7+56huYL+qvaaZNJqI5CGKY83fkyAZI6ntCEChQgiDQsYhZTQyLbXm6CCdFXs+Bs/KK3q3mS65q3ersTqEwwCwn6KxtSSSqBuopgQufTrcYdL2iH" +
            "GpQgD7BefgumCUXs6BRPnPEtbA0gRi/6h16EOtTbbMX35csDl8KCPscmzaWQ5HKHY1gwTJNT64B6VKAIOcjq4wXMYEDIAKV8URbMY0Wmt+9ZLzigrJjH2v68" +
            "PxtVLIbHvCo9ySP08tY9GndtSzFBvwWMQnb6ITqL+hiRr5XyGCQcckwtTyMXUBbMJ+gV5fnrTesF35BWDF/2UVrIQ/neJI3u9SbudpdtTW1Diwv6f5EX0zRI" +
            "gHJ5Z8sFLCoR2+WD6E7/HecXHMqCkbWXMylL4gJjxajtwFISjkRvySdsrCjqXu8C4NUztDRBT1GTl/sr+C0VIg6dTMAgEtKM94CxYFTt5QNlX5IoIK2YfBaD" +
            "PJSnmbnPr/ZEwajL1TW0VEHfL+S+mj0KQOuVYh6TCuDRUsTU8iIeTS8+sGDyay/vP6qmRAFpxbhZGHCkN/xXUbBgXfv8CbtTW9NQjKCnqIl2IBGw5xxiUXAq" +
            "LU8jjAXjrr18zNBia01c4KyYwtuB7uw4RKep2uc313W/mnoWnKCnUmZlbCUVcuek0tQMYwMxFkyh2sv9/JheXJIoIK2Y4lToGGreS02iqQIGLT3ACnqod2pl" +
            "1bGZiMea0wFsQ8rAWDCFay+H+Rsj1kaigLRiSqOAPEQPTTqjfX73i1lGdyhe0PPUquViPocx5wEBh6bS8hgLpljtpYK7WzqKawwuMFYMZjvQbkfdMZsdotOE" +
            "RMVYJgc6BL3ZoBkdd05Dcdhvi4N1e84YC6Z47eX/GPewOj29sS4NacVgaB7KQ940kXNKunq5nKF4QS8aLYZCHnvOPTLx6+haF2PBlFJ7YX1c0CYji0BaMRgW" +
            "OETfHs9JA3fXteAFPc/YRiEVvdsJk+Aoo4ixYEqrvTwu9onOcDSkFYOheYgOUSFvOWfBfX6NgRf0ot1bysTvtpxTEmQDKMaCKa32AvTB6LAE0orBsOrd19d1" +
            "3mbLdJmsBoF4zToYkU3FBAhlSpXm0Da0bLdqFL5Snvno+96czWzuo/ATfl8XHyZKr718MPaloyGtGAwv++J2swa/jaukzIoiL9+mrMrqSNqQZE27wGIQ0VQ2" +
            "XySRyhXKQyujDjTzCW9460oMpE0jXbJPROwIP4QFhry8HgqMqL3weHUtGAxuO3CuNw/fwKqPVu+1QRNVGwTGpWVe5LkEgQFIL7HeZq4jgkhlcngCkXhlKxNY" +
            "dCcIlnlaM6D2FERMSCcrU/q1tGqR2+uh+NqLd9eCsduBi3t3/4s5warO5mD3bus3QR3VidqJMCmCxTKaG9wmhiIcB1AZrKcbD7sEMxEEC+wl/RnQPt10oJEw" +
            "l0roSjCmOLdD3u/xW7nI6PLfl7hzh+xufbRs7dwu6dcaoaljy6x9na+Y3scIDIHESJ3J5tLp88Q4JCwV7ZltRIfGqyYje4tPz/SDKcrtcECFXxQmzH//sRd9" +
            "8d6tzNpoSPpNTue1bh3bvSpM2XXc+zgMhSOSBZQqHh0Sli1OW5LoS7xDYvT8YIpyOxxQ4ReFQ5pcfT/NBY+eMzWpz6Asq6Jxx1T5nRbgcu0v8zVx0VfS+zgM" +
            "2chHEUpI63gnU0cJyx38OHKEKn4wRbkdDqjwi8Pz+HrVNzQ5vd+pUy0TBtuHeQ/YXxRz2vswpx4PhQ2ndsMPFCUsdwh4QnUehh9MUW6HAyr8ojC4+HJTd50D" +
            "bgjayHQKDck8uXbzjrY/L1W2xwctmL2PEQgjF/mliZGT6liihIXaYNug1A+mKLfDARV+sdivHMaqs9cLYTHyjpWLR7njeh80iUZficmEs0yagDuzMkpYTDHh" +
            "B1OU2+GACr841Ph8l099gzMetw2OQMe+lX5u99PnlC/pGfc+uhCiFTQel2OPEhZKwg+mGLfT1RXxQq2ayhdtNxc4OYPbB63tasicdK7eRzT6QpQk7H1b0X5v" +
            "0ihhiQqw+MEU5XY4oMIvDvf59C5d1N1Y24y4oaOYB2muvMZPK16obEtM3PugxE1zmdONEhbKa1Pvcw9U+MXKJPGqHPpmzTrZrvW6ULwm7+h9GGk4kjAPMubv" +
            "9KOE5d0tMPU+90CFX5zs//guXJuxo02Yjqou1NuoRK/iRJH3PgxikkDJckQJi9em3uceqPCLlOEfwk26aLssLBF7gDc4bhDgOHXv485sS8424wTuuuWIEhav" +
            "J+9zD1T4xUqEv0fUI2IuETSKVzmqSD2i6H2E0nDkknJECYvXk/e5Byr84sT4QBXIW7YpCzBNhELUzmfvIxS3Jx8OZCEUJSxei/e5Byr8IsX+nM3QjEyAbBPe" +
            "4Jh1DW/2KkTvI5TQiBKXlCNKWLyevM89UOEXL2nx2oxy3bIA84BzmrpgL07FSpHrbE17H0DliBIWf/DdJ7hYb/P1dUHmsLfiH+U8bmCE4pMPDwcr5JUKjE4/" +
            "vZMlL7dPyEd+PwVwp780rlH/dJeMfC8gtrfH5AR7XZXXW96Ge2C+vq5Ao58vSmQ5FRUXW2XjfIJNDJT31lq/xjV6gd6/et3oHm9+y1udESH0Q7tz4hXM/Vu6" +
            "ntGqE8/+AkAsyNY99sNp2QiC+vHb/R3ucar6zH8Jh6M49vWy67CQNAZmkwmVqkXzzAsmskJsy6+1+cvc1773yBNPPb+mS17q1heWuPt36LSZNe1nAtKt5hxA" +
            "UuwftKwYq3pbedjBEPx2/4QDs0sCeWa/msbstHI5Sbflsp7a1heYcCRhNGrs4zopc4p8gRkE94WvXeuAazc2mHL8bZqc9Cl1Lc8lfndYT6OuS9kuACRt5gxX" +
            "JDNUy0jV20rTH6O7Ir10fx7sQ7+4JJCv9Z+R1Smnqo6UBtyROIfWcnqlguLlWm6lTgxiEIeTX5PnKl3i9r1cZ21x2Bo1mCXeBOl+r3VGtCAvEIYL3YYEVSHn" +
            "8zfzJ2DAL7EAXp6/hLFD8APVRfyNkHodzTGrlZ0R8/jffFRHGlrT6aVaKSDrWk2XQh1EfGW+dJVrXOt6C2y7kg3PenDj9fYEl3MA6VEvMhwRqQtwa3fTfopL" +
            "csNAKuHT2y+9ZZElVbXSPyjQco7DHByeHwkJmCsVw3JoLGso/renxsm/qL3WlXgiFtJkS+BftdgR7ky/3/jBGeEMVwOymVSwqypFm0Le78yk3xrAo18lv7R+" +
            "S3dGlMX/i4FKbTzafvZZd7HTM6Iec8qlMqalZuy0TwDWOj8in/pcrZYAaDXYAgR6BzwB0gyeCjBzkKa/1D4xPI2DGbrLJUNPvcKkIR0THXkFuwky169k4yIo" +
            "Is+wbOc8UU+W0SLzBTf+3Zen9xx8dcqywf4J1VBj1e3nVmpH9ILe5DJV2Y5KsfMQjzD9KLDHIIy+BB8ZewXIqrJh7rHQpO6v5RER5e4VUwXtbKM9yiv9eGK4" +
            "Es3bf3i7b527uApkTjUANZJGSSu/0Ax66b6iovZM3jl8yqUs9N0VfKBfxq5kDcKq1sufaOK2IVLkzU8HU2EUUWS6yR0pfyI4RktJgUBvGL1kF/yiQSlQ2joF" +
            "5kCEad3JE1AIce8ZfBIvLjc3xMi+jKOPhvX1XGAT7uxQB+9Nl/ZbhTQsrNy9jjxjeW0B6m2DNMWlFaaQ9/b9Os2yzBPLwTJHPj3xC7tRUBxAYTPRp7Y5y8BI" +
            "5ma0bcwgo3TEtiblFZuqEDC3J6DC5MoxA48Mo/SSWUYaPdo470+KjbFltOz+2u6bXLLcypzqSuCdjUzGzbVMNOjtjWAR5gb25IMjpAypWND72qMpQ3c4r1F+" +
            "SvDBu3o2ER3JDU0UTNdwqZ5WnnqneUL39J4cOZtrbsFXbUQOMPQGwGxRdZo3FpWlok2XKuKQYGNd22K2AR6ueIHvns2OOucDYF03viLio+Skn7X84iw8L0qv" +
            "mGeisJxMLqTJoUeQt7NXJzhXWWjI+HyG2aLqlGVdEG92dEguBQXp2/so//E1qFgYUWA2w8zk+8olYaBN7IkM8Xogv+c46kPbFjpFPjdRUJaqYI0DeTt0nWZ2" +
            "lY2Xk8HItzdqzh5e3FWdLUHkDhKDI5ywaIB/0AMBlAcEAoU5wUbD5Qre+AA8v+D2h05GXEDRWDJZk2o8+7OoHeg4719HwSxj+QwzUqlC3mL7SBi2hcNCX85G" +
            "wLeIOH/nFkB7/MECgZ5csuQhyK7j3R6YEpiy5iM+XsNdGzPYszcFu+In/NFloFm4hjw+gxOsRRX+5+iWgLkfER4nbNhppnvpXWWH5eMM9s7p3pu3jBNcAWdu" +
            "0Q/mVorU4UWBoEZNbihsTSxgiIAygMX1Cx35jNxaX6BgzLu968NJf+zSpRlEV624zk+2XAIY95R2d2gOhQ8GGC2LOvjwcTZkf01rkIriGWfay53jCRmFVX0z" +
            "by5hjAWp4iIBtR8jB6GAom5oDNDgnG9mbWhqMblD8MVQEG5CuCpQ9Cv0KacOfRDg3JSobI1AKx78WdnMLabJBZ7C0sHYzAI6qImc/F964FuCkDfESVrhDkoU" +
            "ym0nVEN9p0Mcg3WFBVJ7F2DjpMhNEmuxDJGZlf34A3MwtD20gzmGhkDhEj8Pc81tpKaijWS21Nx6xQE5+wfyW7Pf9V2n5xw7mKtsuoXByjfL9DOmE2axzdVb" +
            "n/zWYh5fvecj0c1e2MzFV/Tx+HT+PkQlYNNktTBNC1aBhvs8ftafuY8/1py/4jiLgSTcG16bLZ804ErNLG1xRIqEgzHBogIHT4NzyPQbpNMZMXTeir80lvA3" +
            "wRnfgqYLOgRDgUw5eN/nN63Nr/gOF8ZfS9OEMQuHg2fP/9re3+TtZy8K4dDySj51vmRruidHfEYmtOfD4h7+3avO/4Xr9wypZin37ExM5EL/uN7v94+3rI1F" +
            "KEjbjeMBcDJjQT+otsGYYFGBhGtIakqcnyO1WiuGdncvrFmWDPI13RUNCk6QFCbTGr0TmcYVPr1Sg36HLlSq4z81+c23Tavp6ze5qmhLAMDt0NCKP0Uhc53N" +
            "6ZKu6ZZsqvN78VwQQfHUyXoul3xL9+SMGW5D+3APj/1O2fvNyIW1UAKdYyxvz22CRQQSIO9F7nGsmEjtphAcjSUMxwB0jg9dP+Cchjhn1l8xUyAZGGp6U9vS" +
            "ZVdyRVd0h91alHAdz/x+d1YyGskci3jN5ZM1Q4sYhjryGursY09eeFYKpbrMVw9QX5JpJLwXL1npcinWbJtywc6DFLm/33+D9Bk8sYkJugUK6Ne+Itq5kuba" +
            "EKnT4BZNNuQIzat34Lzu8KVQTkEbSaKzRmYyyLS2dqXL0+QvE96PbgOkTPRH42+wb2ufWrKb0JnMuSx5Pb5wB/sedvhFGqdF9dDXD94v0Ey4wxmcOtEov/s5" +
            "F0vOJpd2WvyfqOycLTBBl0ACnOrf07Aj+FbxXEjdOkcEwYBBP0AEO/zap/zizPWdQLGUsZVmMMC0tnKlzV3mkzu+2915PZg7r/9RjkoBh2qyi0gTbWmJ6KvG" +
            "mpT0nyYd6FCLVWd685BgDXesCIsQHUUh76cWRGXKOUfXmvKZBIAMRfyZS2kF1eOQBt7qjkcdq1h/HEC2q65oXfL/zZkgPnI4IFsIGWJ0wAtXnVhtqUG2N40z" +
            "kcIelbJglhr/45UzSIKHQ0R6wlJHvKY6d/ST1kOD2/9wqSVqpbcOFWi/QCuWVhsoYmDqxEvUTLRVyPNI1gDwyeog8NRWUD0Oa+rLSBWTiGIh0Ce3V/cKQf3f" +
            "nJei1R0oYdwXFtWlE1WbfGbLDbHAdDA8pBm/GJg+nP/44GzufioNACk/GJ1/cJUm3JMFvp+fNKtZS9SZ3z504DwBDzRjoTGkeZem25WGTy5AG45tBeXjsKa+" +
            "gtFwqGLjXHOdROpVs77paioO4swgZqC/sFibgzpRTRc3mVFnZyvr5GmCWx99jPwPGswpwzJ1zZzAv5H+U4KkI9zSGWWqolYyQQDKz9GGNJQOwwyyfuqaF1Fn" +
            "HoA5dqwVhMfhjcNHM8ZGwAWbgoBUM//PnqwAb23knRdnSReyi+NhqherlZYabIHtzMvWj1An0wRmZXbd//XB41nK9mlGDIf09/WanAYyF9AS9crvHBZIQPsV" +
            "PNGKFZlAKaTJMWrML2Q6rVZw2q1WK6zRakBGSNaRZcHCcqB7KaCTj2wOrGRcLIEXtFnluBkscr0xpJBpEp+OP870V4/VcJxBCg1h0+jBfNFF7iVF4qVMaVEd" +
            "ggIskH6BdrSQMUC9GSySV75oBVW0wwcJiDM/15RDPO4n1iBjIEr2agEDZyHh0Cxe7lY7bkeDLXSdaWVDXqYZ8zfrEtKfbZtGUvWAqKOH2ivIeZ7CLcHPHoEG" +
            "PAC/hCfa0IwlAYFbzQIPUQdnNzmmU1abyaJk43ma/GY90nS41F1GqUasLiAiyUQWIaAFi3j3wKCDCIxfQ4cOtOLpaHO0jIRAgeaGf+DV4RYnpU6nx5qGOgF4" +
            "X5Oo/epOIO6p6U5Y1GT7ek2xTFkM2WCAlPfgEhq0sUuWUEiz+RXEDnJLDdFJ3FQtaGG+rymXnQJGSaI+bLS4RNgPW5F7RZs1Qck7TuBUaAZFTQ4Dp1+tglCJ" +
            "4pBfvOjhYVgPXKqVXGP1gM0EDaau4nAHLFBBAAbWjq2no8s0N2vU+7UTPm/QSSA4uVgkc4f+V8J8b+TUb97eGW6oKRXq7KIhe5ZA2agLsJYILFB+BR08q51P" +
            "Ys40O2y6NjvLvEGncAVl5fvCnc0zxQ7bpJNiQINeRB3l4azx3v11kIAD2q91srZTzY5azD1opZt83oQodoExMeVQMFDsR53kwrY1Uh9mtOm0RT2AwAAl74EV" +
            "tPC0dUEzJ8hXvwHE2QouneDM6QgM7wtTN5eDqTtz1DlYSqohIoAA/VF7WQDoOdC2VIdEU6NsDnmHb/TYy4N0rLZW99Ee7RsDOnxdzIYe9HKPN+PHg34UyQAI" +
            "Kc5+62v3MAFD8Gj5Koxh52jQ3HqRrgKP1ZFt1kM8mb4h1An77q2UaKZFcj1Rc+++9OHkseWpzywAGx/N9ZDgzGcEPrC9VNvDg3GzvpDhZcBvVrLkpuZz716W" +
            "5HtpmxUAHdcw7xgbWS1PKMOKnp6os9y/S6wThby8S+rfi6F6C7MafcQxh8M2wu/Wzw3rSUGe+iRSv1KwTc1f7WqbrBS5kaK2tZ0d7ObwcXsVGpfHjbq0u55f" +
            "EVNsJzhYqOauRGW5fs98Ro7VzM98e8cts1z5GcYogfwqYEKdEC9qCb+Uy0OEuFIth5GXaLG203WXnXQJHyaJTI163W/ONY+eIXvNSQGYbu6KFWprx2BY3BiT" +
            "4ftWE3VJ4VYmpF7zV6l2AZjQpuYbH07nkmyFQi87w8i8Kjd94gmSzuES1RN1jg+HYYgxWSEHzUhcGD6xXHeFMqUGjSCo7UOT/DKtmutbmThgDOJ2bK9Kpxc1" +
            "h8/dX0AWY0wNZHN9plF4jSVDQpSwaPVErfBxc3TVY3Ni6USIWNAuQpV62URRc2vNRIO7dbwLNcgbcYg5zXItWUNran7pu+tJWEgKXC0fo2z5jCP3Eg0NPzaz" +
            "FF5NUWf+vAaqbosRPcOX6mzTREgFDwk65MNMqywd6dJpwzLtUm3lPSRYhpCn0uGqx91j48u9w5b1qB12xj+z8cjLKeFx0roMyo/9qBs2Hh3wXjQ4+sXV25cL" +
            "4rxVpq3zpVy8HeWZwTnd8pVyjLblZcQsCq/KJThPL4uol/k6R8popPhcSpXY2taaaiQZjIuQu8UydwV1pGNN2fCreOdVZcx7SEhqeySgBwAMJ72cAktUpioF" +
            "gJ/vTff300dW5QFuVwWShKIb/RwzktIZOph7y81rcGvp8fwTjaNeENDo4v2whxwiSWIa8aqys7kZMmXupdd8djZWVPv2uyZa+sARI+bK5ns2e0AfTDw2ywNw" +
            "zLbakk40bTG3Cac/ktTqAvQdnaaFUr4RAACnvPOE0NL5BmeZv6HODlx5fq2xW7LMpmcchuIK43DD30HyjCNJJFjzwqVjuIuldgb5hkWaL81w8XEwTuPCEJwG" +
            "V4PRttGWnAwySXOf12jVFLXqd0NwBropB3xKryN1tm7SghipYCPNPtUMo55qTO60QRWfrEs1DW0sJYeFyAHws2jywJHAU1h759UYeFDXJ1agcB9NrOl5NkhL" +
            "FOVhA9sFjXyAZ8Ga97sHu8oWyxm69xyB3r2/+AWh88Q2slOVxW3qoTDOZDdxsr1IkzxpKhbwdl8MWgFQj7mI14f5SavB21a9GYHBqaLqtMdTvKtjtfmG34+j" +
            "4JsAqFHD0MI9Yp2DZwd92PWAx6h+C18pYxccxMZ7tekZ5S/WJ0G2/U4z2PBRkp9pHpfKPRLDsifroyvfm8H363UlqVK0ITBziJ9EncuSyT9JqLdIU0/UJb7b" +
            "V50/UffCJU91uL+ROT5MNQuzO9CouXcUec07MiY6RbUTr7JREJhg3ZAw3T+oyhzAka2tey8uffYEiDAq02dx1e4WqEzuypneJ4CPHXwydgl+oBFc5uNMt+S6" +
            "0BSOHLnB0cYQLtdmggiK1RTkNnWxfy3pIqEhod5nCfd4iu/3dRMFeXWwGnORLOHxxghEzEzBLtDBVdbSAW10nuSPoBEQUPSnb6LAwjUsUZsczJ5k4/2eXA+f" +
            "xTR1wRKpjA4eO8E8xYNLxGi/QM4WmsyOIVAarP0ada5e8LJTc1Pg1CEemo5sw9Wnycb2OIIWd/f7dVNlPtR9X5OCpSrcHmaxJJNpxCWYM2lgUN2A9XiMt7XP" +
            "oz/MJyiIeptQVI1lKexoHJad4D+CqhZsTLP/LEOpNYUtVhkeKRqEvBte0XQWpImt3MDFVzPaAx1m4Gu5VIwuEbeZi30UxZks6aAhtRWXvZXOaAW7F37SXKRz" +
            "//03osKcYSX57aJJ+7y768gn8kWWQiVelMngJYNbQH5LAM7o8NZ8Fl2kcjFU7qQTXDh4VY+G4BOcLEszttU8qRro0iPXjPutmls6RbUfHK9c9uyMZuf40HPN" +
            "KXviVw/rAel4RcqNMHtoemduKlmn3e3CExj0QYJ+YHxU0kWZfM/A1IbUqd4imPPuh8z0ZxkITjZHeye4cPA5WFgXhkhA0YRmdhRIELXqxHYiyE7VhsQ608R7" +
            "g9PZsgHXh4pLygvS9F2DSwbRycfZiPmaACLlvbylPSaz2ke+4s0CMJmgcmRqQDP+tkNBOmPgynTqs/S5cksXnWAVlLgCIloIKJBa9HmHjLMmMv4nYjVFLxeK" +
            "0RQUON1b+lAYp+6qUlaTHVWLXFPxQN3EJQYLnc5S7b2CkBW2W9XNCPOFL7hTPFD2LQDg48XITFGnelBDV6P5zFHZEvNOsArKsf1NFeV8h/b+bTykhqh0gWZ0" +
            "7HwBl6SeRqJkddlLL2NLZEEr1Pag1kkUVBum48gCwQXvGDaT9iRv0EkPypl8bDP/Q6BEw051T8oih6ivuz6LEsWTTrA7CGOLsm5zBkgnM2Jla2cq99zZndf6" +
            "TTgIRGvqQBz08/h06ezmVgx5axpT28qVxSFFsPyOvKr/FeDBL4ABCGxwwQP/GEE4pNHrIn0LZ3AOgu9UU3nPjXA13X9Zjb3zIZnRCXYHsxZxbObqxR+5h6Nx" +
            "JY5t/YLS08EThmtZLjaY9x+Cb6nu9u76xAbeZ51oZaPq/LRUmhhSEvvsTpcdaejAF+BBAhV0MMECCAgw2GeDA67nUR8E0OtU12UtpRwDLKS5zx5PqRM8RW9s" +
            "268/fD0QoXgGHYeefEHakQmRAQZK2I1n8jdiBU/FH+u49scWT3RgfdrzLmu7STSyDqiJp3BniAi0beUP8BU4EEACBTTiBgN7EBXRqa4vIF0LGWRUppn1thNc" +
            "aGx1EhYFpVUJI/Se/UA81AuCTk1wDdslLLX2/5MW4/9TOPrmczwwrPd7e2zsJqO2ph0HnvlUmmooJhOa4c4+eIPPDLHAg0hLfeB8YYpxOxxQ4Rd5Un9ASG/6" +
            "heHDoHtE87qRdsNZYbJCIf7PjMEXf4x77Fu7vTEwdtFer+fEcymnbiSEY4VWeEALL/AJADCIC/CFKcrtcECFXxzgirQ7zyKVTFvDuf/lU8KA4eDX+eQPeYfk" +
            "2ho5J26lgZ7EcZXJmvdci2GJZrTBEzro4YcQzhcTyu1wQIVf5En48iR3vqLuEZa2G+FzgfIeRbvR/BkS9nOaUG6y9oKxD1PrJPdNn0vxYnAaL5BGK9qF3FBz" +
            "jh9MUW6HAyr86jjJXSoMOpe65lFKOP/v/HbJ7oxQSOaNL2nyDR0vu1ubffLPuq1LLqupow0MfVihpf3ogEacq1E/mKLcDgdU+NVwUjoRodpm3TamOko4K0Kr" +
            "S+sO/XdSSEKakLma97In/66FtlSXouE9RQNRSiZrxiV+MEW5HQ6o8IsH5M1XFHPfUpRun+7x1JFuEdPBmKnZ+/UWy5TzXvtQJ//1MvdkKVQ1tQwwKTHxRTkr" +
            "wpqi3A4HVPjFIfKCjty5jm2r0t6CTn80YPUIYQoPQkIR0uRL7+ud/terOOM100/RTF0JDgydQoD8YIpyOxxQ4RcP5rqbqLbtPkjJo4s7FN2LgxSOXCb3sqf/" +
            "FREm2tK5UJV4cULQ4KasBDSh3A4HVPjFYe1eeunl84vrybHQQcp/g4O6oPMc1mTdUCCp+1H+zj74rmEyV3zUempZT2nue0UsE8rtcECFXywilmC+FNy2+yCt" +
            "33HSe4x11DbZ9hBgGwBpkPvO5FOKOhlHvGZzqapq812DOIb0A1FuhwMq/KJBUT6/FC+m0I4T7sCR26RULwpM44PUS2KQ7AuceUvyhcVy4kjqrgg5otwOB1T4" +
            "xYI4tW6l7ropORLtyiRBZ4aBRLvE30iXAEcTXOG6fhhRbocDKvxiAbNFrVsqTe5LW0YdaIl4xLV7jJXFrBuqs6El25pw3fGiTcG2cEVwotwOB0z41QS1bqXu" +
            "S8BQLuYTdBKeLcjCYlHg00/1VXuM3encJqmTvKMd9QNRbocDJvxiwZWAvzIU3zaQ/IbuePJzxtmCTkpuO78u2EgjxDEmljbzts3kAeO9KdLtUMCFXzzWW1p/" +
            "mNCpE6AnFJoR8t4rE9uUdQ0w524uoBHNgkAhpcTAHo/e5x6I9vxyXZCrffRn58ZWH71dcvH1TRTqUqHR5xe+eSfnZLVb/Ob3v0fROKWAA574p8GsbU9vfYXz" +
            "Dae0s56PPfChiJ/vi1/1i+FU52p2c1pzIyTUyUuy/T4XuJ568LvhDu2wWjqCSGAEBhMIL38RCA9dh7GiZaVr/k2N6uiVqS54OvbAhyJ+N523Sz9tDTKjmYy0" +
            "2s5usavd7E7EHRF/2ldfSlUTTGc3ps1exvQ+IA4LrfYC528RwRFhxA1AwrAWVcfFNt2PaSFGaMhY/tHL+7EHPhTx8SbI4uHxRe4a2xtkBkMNN5OVTjnthIGB" +
            "r2NzW7CqVk09zbyCw6YWN6SqFSu7X1Yy6IWab++nIqdq641w0Td1l0/pFkDY7Llx91c3Nzwee+BDEX8/IGWUUtXWtjXAIkvMOFrbGramRuy82OA78JzoMIuM" +
            "lnc6DHFT6gpEiZdpsliLrd/jybqBjcwuH69lloia1LkVzI498KGI96aT2Zi8vkpd6djpNYvtoAEW91HKdavgB3jVckQ/q0w6OFxiM8wtYSgtV41YhZOboHdG" +
            "NJvrLYA4ym8okXhj/urjsQc+FPHedAL3Rp2r+Mq8u3KN6WxrgaOO+4rBj8iQnI2ddZ2Udbi906W0GQpT6o5O/U0Y3NDCMujYbTPLHZfi57EHPhTx3jT7C+zb" +
            "7HbnNtstr3NwBzFax7LgJ3RUqvKo285UZ97xCg6POCVpPxKWrLS/CTvP45WQy5Siriwo9eXYAx+K4E1fJ6BvkefKfyB1uSt9xL6Yr7YAeB40+CkLByqlcYeJ" +
            "nffQopIVbv90G2D3xwIKs4laL7QwDLlMlaT6eOyBD0W8N83w/4C1R6Azm9miLK1pe2Lv1A72MeoR4ex50gP+vigr68g+MjfzXU3U+h4vLBZxTu0ZJb4ee+BD" +
            "Ee9NCQ1rj7qiO6+xYpktroeBg78hyewSClhK4wF7G5HMm2RwLAsLse+qozYILdwzgyVPPB57eB+KeG+aBjtv6EvpdNcmcVLNS9v/SIGfC28Wq47mhtpzRunK" +
            "gtjtjpymp/shdiR9rMAu5DadcJ/B12MPfCjivamIPnZ++3V2iVVZ3vhAAuXOEazwPDGZJyXKyBimI1n5EZdpM8XJKxNaBGCVgpCfxx74UMR7U+Ov6+3U6dSZ" +
            "7RELIRo4fuGGXoiYgWR5vlXM/kBOyRId6g17DEQlizpWQGaV1J7x89gDH4p4Dxl9QN6iTpnzygbmRI/DisyQhIXDqnhwkj6y0iNMBttFnaCeHd3L9Hzs4b0r" +
            "6L2pHB6pOomQqNR8mclq8MuBSEDpAgOqfW6cXmbG5b//yhVauMsqoDgYwb1IeeTsA3DxI19NQHSZqujDrLN42TgMRAytFcsjitphsajEa85T06HFychNUByM" +
            "4F6kXKib0x0ufuTnJyCaTKGvoqv7PLQJxRKv81Fi2T3VAAYi1KBMkAXcCmPYxHD+T6dbmdDiKDihOBjBvTiB7xQ/No0Fbf3kUqnJ/Y4Obyof2nxZpBJRJUsd" +
            "WCMStHZA60yWAWsqUIwGK58cVZ3FgeJgFPdiRHbFZwZUNIdnBtBkqqIPVad6ZFPR1O0wEAnJgVcMAHrj9Mt2TJSTaIvvRa+wYDgYxb0IUV3xmYCazeGZAAb3" +
            "39etL8VGrWMVmcA86oOXv3tX1mDx5x7tUrKLBYzgYBT3IkR1xR9Yz6Jmc3gmABOvn2lSncT6P5OP/G7yxdaxPjW3c+Kx9OBdcXWYfmo23vT9MMkuFjCCg1Hc" +
            "ixLVFX8gHY+q5vD0AFd9xJVahariGGlnenkTP6XftY71zdO2xXWFSg4PuXlX2lagBndeUbZ5IxNnHO1ikQLBwSjuRYi7K37/2hHNbw5PB1jO83lgSSv9ytn4" +
            "BhcYPymDmJgC1i+MwUBL6V0Bi6UK1xLzrvSsQE3af0mT9YpnV55/jHaxSIrgYBT3osTdFb8/YkLdzeGpAYvsTjtcCq1eeJVkGa1vcIZazuJex/o1rk0kwxNn" +
            "5L6YiyIIHa7F5l3x6jAde/hCisqy/oIDXDr9MdjFIiGCg1Hci5DCXfH7EjJaqDksjGX0xhNiaSbV+nErzpzVpd0trSXivwsSXdBUcAw1ZivicGYiyIXsJqcI" +
            "zbsqD2Y68QXcr9Nday0cHwSFIi4WcREcjOJehBTuij+qnvWCFmoOTwVY7GC4zAMuOci1fpwKlbJg7ct0RpwirP0vh9FWe8kGuu6JOb3EL8x9C5Y0RXapZ3pX" +
            "AC2mFY6eL902LotuzJ5EfIN/B7NysUTGcDCKe1FSuCvO6rqyZi7cHJ4MOo/hYIMyaigutEplHLsDxmXOMhrNcBTPrGz//IisAHnK5Hy/f6KzydOkg1DTUSXz" +
            "X/rXYnqpYz3r8fbtGI/zG/xTOEaw3qx684/hYBT3IqR4VzxOp65bWqw5PBHgKqOfHqTK0Jh7JEmKsvfZ2x0e8DSUflwV/Qu/f2LQsHyJrqxfplwbnsaaSfX/" +
            "/FsbynI0/ZFPNNHRKdnXvT8RNrovESEP3ysZB2O4FyXFu+Ix2o3c0uLNYT6e/OinJ9FrFkqbBTiNi6qj85E7W2bi+dJW+egvnOVD2w7llbkHPL8DBhFcnOQb" +
            "zie7i2XamDaQLRvQ9u2YD/7MN+2C4mJB5izUuHdk0+pxLyKV1hWP0G7mkcWbw11AFzn+qVlsFGcpzF0aoSwuq45WdgcPy2S7tFd5eyYuMtoAVPN8jo/36yff" +
            "abROCAc+pUdBekzrqapJe7yJ93UXj/OF0IcJDBL++kQQ3xX362XKltIc7gD6ZMZfF6GVK10jYTOPxEneacMwt5AmeZrPs7wavELgNZYiXUPzZhaDT1uot0xl" +
            "KU2F6zSpJ1Oy62NWO/ZyVVkWsUwodzXT2UXM329CkEC1UGHu0ACnRSBT/fwkod7glvgcr7t4kq7DFc91sof2zfEB0VKG+2ELNNJ0vifr6IjqhGonXsveAQaO" +
            "i/XiBjXXf7fxvegVZ+ZI2KlLQnYWBTqJZTIGb5oshmU5M42FSJfweLi3gCbxiEkG8fVAuk5dA3axaDqzbFtAms1tln8PR2251A2UmDvMwzFClXySrHJ8zm+r" +
            "zmkNrz8+wDKheVssFgOVYM6cNwxv7hKt62Rsow/+dMQb5G43NdjV/eE4WGzlSrVWnT+ABihJytKObOs873PNGatw1+GaJjnH+ffsm3qrTqhlEq5TrIl4aIOR" +
            "+ciXHICaXboEMwmdBIozdJ2wRtyVxnm7wwdudk2TnuTVwWYo4aySZl7Nhr7Xu9a8owOaMBwx+kHwoyan/2Rm4b/TkG6gwvfc5OEIuTFdC+2a+hrQtyijOnm+" +
            "eQNHmTRq6+TC47iUSnZ1Ay3s72QIadXOFzBQbSPeU1bNQ89jmdsPQdOH+g6N0JlewmrvFYasLqtWOoFJ7xpv8t6bnSGXJ+AHsZErxelaJ1TbBbnqqpIqWIXb" +
            "DjS4goUm9EFJiqPRUiHyBp30YDpCk1cz+ZH7WdoZZx/l3q58LGQ2PPjha52XY6GFF118ibOZcuA0iRBz/x+O+YewyupH8LFuxvNIFt/KuJidVVlt3Y+Pt2iq" +
            "/7C66UTekzlc0zX0mb+8mvNxb14FzIaYR/KhH/P7+nbwJDg6WZ4dQ3D5op7lKl4e+/v5hs4upmbwE4iWpF7UQLZJy2Of+/h6jNb2Pnew4Kx6JClNGT1Df+o/" +
            "7P0m2WeiOazatgJPnA8ZTXkiYhnpcA6XcH1s7+AX/O8Jos1UymxOlmgL98f5bt7tW6LsskndrLm12y997R+UJ2HiQ+PHKCTUU9Yz9L+JL/i2WKTzJlRMc/IE" +
            "Tds2oEJPUgw5zVjKU5HKBPwiLJAgeOx4LUqZ63RJ1niL9vB4nETNu31xc29t2K3vXe2E/lAnov1Yiw+Oq0S/nqHP+evOZLVBJhfcJBOWDyWqwavrXn0NK0xL" +
            "LWgBfhWAv0QTIWPPXlMlSlU87C+XHdGBVSs4g6gDDx65VlruTn0svYGDl9n1DBWCftHFh0Y2CYdYIpnyfCxxBeseC+4aUKMK/DrwsIAz5oSMkHa8GfLsVfnc" +
            "ZF/NgrPLLJ88ccC9XOVKn7POJcGo8w49Q4WgH5ZCHKhbJQhD7ppjwbMX5Ilr2PRt37VAy/cjhRlG9aydGkGkjOs+lK5JAGhSOctzTz2ynWtsS/uUA1c41AQh" +
            "6CcYkiiGwgxkLXnGGuoEuPceNqx3oBoALb8Prkje2YYBUcA6/ppqBAuUBfNYv/QwfsN6wQFlxTzWz7fBt+L5IjoX99gB164byWyh0LMoQT9cAoUmT6pStXLk" +
            "oSVUKVpZMTpAyxtwAhKANDsCTnGUBfNYZ4/UX7FecMBZMY/01vdidkT3mvzgkO3da91AkXGpYxIZH5oWKegPNSQbLkxShQtQhBHPzNxkJ9DyIchJY1pvSFNd" +
            "LsqCgfN7OZN8IZFAWjGCpWEyondJRz3e5Pje7uXqdSFNQXrrGZov6O+SWB3uX4N5msysoXYJoOUNuAUprIJjvcLFWDCq9lJKooC0YkyWgvGI/q0cnzPWut7W" +
            "5pcteZZiP1QJ+gqGFBKqDWNTfIQWpAmZHwm0PIVqXR5Ks6zlgbFg8msvp5urEgWkFZPPYqiCGNzacT/6PlG7k93G1M58NXUtbkF/0PAUQY+7k6qVQ+F0G9Dy" +
            "HQiMQLwYC8Zdezn1TJO4wFkx7u3AIglR4UDqhL/Tm5UbrNSXfdZSByXo/5YxlZhBmQxhnGbnoeUpUEhRF+YgBOoNwFgwhWovp2p1iQLSiinEQqhqObxnLx3z" +
            "g9Np97Jv2jblybKwoI9KqEloDXpqjXAKa3mK3MGDsWAK115ObvaJC5wVU3g70J0QlS6Qd5oYnYLudWmuKd2CfoMxVGNWKqXYSKpWKC1PwYAGUkEQxoIpVnu5" +
            "qzHcJApIK6Y4FapYju7lUm7yecs5JU9q7LrasrCgpxJVDIVTU1daPgQplo0OGAumeO3ljno8SRSQVkwpNAFRD15v7YRjc/INdWtqYF3hFvRRQ4xBYcaZgCst" +
            "72rc6WALpnjtBdB11YIKpBVTIoEganS75+18TvVhq3X1RTFBH70UQpVA4mTuSssbQCFU2wWMBVNa7WVfPW0PCkgrpnT2N7UcD3Xku71pcMTkdfmLA6xpaGFB" +
            "b6BCtYNAJWl5iigGwlgwpdZeYlUfzdwG0orBcH/nvw+uxJ27PT4P1GuSgP8UpvsS/YJ/6KNiFI1L6c1UpHlq8rds6tIiVIaq1wVgLJjSay/J6vnAAGnFYGiP" +
            "rmTZsme3F/zndCjaHo/7D5gmDn3xD308Czf6NGfc1rDb6w2reZWVopSFKvIQvS4373abD33kAK3aCzgMcFYMajvQnlwQ9N3WgiZ77MGUsvIvpK+vKUQF2ZCD" +
            "H/Crf+hj3ij6A7wq2Z3valrD0eOOtqxJs7vOqtcqKtlSKq+H4msv3l0Lxm4Hdk9/7r/cUws0HzvxvDBw3MrBoOLjS0sz4XEiM977AW/42fc+FmVEfyMGTgWP" +
            "8xj72va0Xbon3N2myp8fTHFuhwIq/KJ/l4C8rWqyAqfxyMDuyr92nnlVyo6TkROFV3QgCy/4yQPf+3AU1xpFFygVbJ1bzpvcLKPjMXT3VFdCa4pyOxxQ4Rd9" +
            "Un/mKp+y1JD6ptF06CtusrkWPUsNRiSe0YZO1PuA0Sp0CVHCcp8QgKni5Q/THp772vVmERjTqGc/mKLcDgdU+EWfhB+/nZQOTC01b9ux2m/e9qWUrwlSO15g" +
            "jVe0g9xQc3BOlLD0Y4LVFf9UddbvoqhGuX4wRbkdDqjwq+OkealMeZDy0n2US/vji7rluhIdh3QYGBFE4d7HRmQQKE1ECQvVRaOu87xMWYw91w+mKLfDARV+" +
            "9ZyULrcNgyPNuuooZ/rrS+3pUqpmwkKNUjLtmfGqIdgZJCxGYtX9b/bCNCFzYKF+MEW5HQ6o8IsH5M2Nn+I6SDXvFf7aKs9kLXQl2gmMZGDt1jhbsE6IFdeU" +
            "CLry/+CQdcUyjQehfjBFuR0OqPCLQ+T9FxOM2LaSrVisFYgw8ZbNT9WUNYS3CKzwHN90qiBhEZKqITyQpiwr4WCKcjscUOEXB+OdbBPe4IApdpC5f7EOG+3p" +
            "0vswJlE3wNUyTzyChGUn5u+4UJSM3esHU5Tb4YAKv0gEvmG+iNLubGOKXsUL05Gs+fxUtWinHmLf9twfNVWQsFB3EJDgMv1ginI7HFDhF4s0+8Hrxat8laPQ" +
            "udIml43virfeR4zDEx9Bp4A6OkhYQlwI2Ep4HFFuhwMq/GJBGLVucO3VoKF6FTQFnVBtC9CGcE6tg4SFmjcMin37YUK5HQ6o8ItG5+e6C6l6nkksTJsIZpvg" +
            "Cted6qopSFjakDDWK6AZPc49UOEXK/LZWa3brep5xuxVmL0P2SYvkKrLezoRJCzUbuHxevI+90CFXz2i2lTbdl8CRK+CxqJNHhari6gbGqYOEhaK2oTx8Hz0" +
            "PvdAhV+kcCXgrwzFtw0kehXGp53qDChFXhahDxQkLGEUWdfr0fvcAxV+0SKfnd3PL+p1ipuJj2N0tdnd8rpBfCxBwhK0gj/Go1p9eS7i3LBhs5WxKOJ++vDg" +
            "NsAF9yrfbG8fBlIzAZKkSba86s1DZWklo3U60hyPau5poDKoeLjYURYQrPjc63iQt+kT0VBh+JsspjsDesvzoLwHZQ5B+bJEUqbaMXQF+7NCQMNkdgZL8jB2" +
            "wbbt6ePBqJJwSKjEDYdPCYkuNB0izjGlLhy6T8aj4VQkd+hcFdE88MC+NHWVMJRoD0KFiEXBCGZPi5kIrY+ZJF2Cvpdx8iKS8BiEKgvscPgEG+uSBfHdl2V+" +
            "nzgYON+ASiZgUXBFcRwrySW6F+E/nP6btc1AIUzEYZAwKvHEMdqnwFKxqRG7eKN3mZQkZt12m159csTrzpyREtBR1HvYv7amoic9Zs4znFMNNOL1SbubS75c" +
            "KmAICENe9A+dxiyBQtV/ZD8f8eH16ZwIK5E86LBh4crRiOGYyN933N30nbP5HOcCgpD/Xg6RbGUhkLr1dxfyZMslp0HNODB3T7GA5dgiC34i009Vo3M2ovMl" +
            "igKiCBjMS6SBiCt0C06Xhqf0Rr4JW3cd/2r/4dSwYPHEe1uKWC4xf+K7ry/lk7U0AoJr0J373jWDTm9PvFLCpQV+lHcYxpY9Jlb3tMq96d5SHkNa9KyGScLa" +
            "6oDjUmECooge2rQtVw2bMERSCWrLA9zajMH9kB1o2NL3Og0L4QG7ra86CAj1OTQKMzXtDOcXEBssIPz/aER1pIZwlVFKs4NnkaiuUDTjn+WB7hwwdptDxUBF" +
            "BoigrM2WU6AXjcdG6DPBIAEBnZJEeaDYM3eVVYMShl6s3Ravppw0Tsw6wjm+M0PuiRiUjsZkDiVFL6U3ZJDfBAMEbFoXS9uD+cLEVXbpkZsdclFakv6X99nu" +
            "uHD7gLkDN6/BUcFl4FSfJW6X1jehTNAvYLopyRVViWTgKifnUUsYZolcszmUGGcvKEC7ANihu8YX0C8vUDgv77aW2E3QL3DTuo0nEco+Vt/r3IwyKKm5odYc" +
            "wjk3AryqAIAa/AJ8AfaMgMKVhA1R1QSNgE0rzEI5pWn4BqkXdgYDrZQWppR/12kZqmAilXgAzEhHYUwuwoYkYoJGwHQiB+9WgHX27ew5vJjoUWvRXKJ1zb4S" +
            "kaFOuQDcRNd2L0+EF03QFhiYqaMvpQ1BsvH+Tnn7si0W57GjQybu5MA8p4eu+pjDaALkEGKb7FtBMngrU6EtsJ0Fd68lPlna9l21BJ9DxTS0ukM2taQZxDZA" +
            "K2gFBW2zIwrrTkaDO2VhTNyjqlJzzu1S0e4VS2/qK1JIDv5W0N7G38WgujwC0AENaWQXAt1sON3ufy1qsDRjCP8RxO9aDAAWwB952Cv8wV8cgfi+b7qpV5E+" +
            "ccaL/kBr//NEQ45NEeQn3XPs+9pPHxyc+2N+rN85QmusIdH8pJVe4WcWx/Jpjeed9KYtQHziilXpD6jnZyp7yDY0gPzd/oWT+5bCT8FDd7Xb9WNrf7P441gz" +
            "evH8+XL9s/EDXQu/EI9BlGw0ePTRfqGX+7rc3M8kGiuOFzDk7/ayIy97wsb3gzNnra7q1hdlM+mvZAbbmtEL6KMgPnm+U6pY39wPfj1Oz6o5Nqgr9LwT3dr4" +
            "1vG+0rY9XXnIQn7CE4oPfYSsgs1VvfFiDndCl5v6wW/WnGCpiD4MeupBoXNNntZ5tNzq2k9AE3rTvVKxHhJ7QBXpmFj5qRyI6PP4edV0vSUYoV7vXLVbjS9z" +
            "FcbPWcuRtkoqJGaG7rokydYTtvDqFnOFQDOhIcsOE+tuptYTmvvuiZ6oIn0o31vuBC9FM3hFjagJt+0BhYcUgeIkRAvpz3iaukPtIdb/7eZdnM4DtJDgaznG" +
            "INY1SeIES+PJdM0SG8ErasY5OVXkCpA4xTNLL6wzKzpli8INga9xgTWRFXTvjXSxLWJdYLlqF7hyc0hLaATTLXlHW3FV4Q4gsvaYzBFDWIQ2iO0RNJHWVque" +
            "115kLdsMyibWHkmXWHtIMJmDkVvICFRzLNwNCLQ+rffxv7Ovb0JHFPiW7saz2LdwAdHa8zE5q6/aQkl8lVPjD9oTH8nZwyWRNRjgqZz5AYEkRu8cR+mIsCaS" +
            "hC6FyLK2u2y+am0xkuWfkj9IY/uHmdUtVobcDgpwFY7heLJ/wlFbRYiMhY62vI2vQXbHWbqDocEE7bN+dLnGZF5c5Jq5gAEcmZUL6kUZilP5vfHwiBSRadXE" +
            "Gtl9Ve0aaUzQG8yWuUhnw+r8vrX+eeMAW6pFA0mLGiqgIKM1r2s61cQkwAQTjNgnoXVN0zlLauUaga95TCUEWXsbVacqFRn5NK5zZg5WZIWFiV2jXvba93Pi" +
            "wgaE5GtvwXzHkX8WwiVpzfOq8dVhZcbJEXGF0HVmZPwwSM+IvnnbjnP8io4XRDN4QTSDF0QzeEE0gxdEM3hBNIMXQjV4IVSDF0I1eCFUgxdCNXghVIMXQjV4" +
            "IRSDmfIMwOk3Wnvx3z2KMkK2+KsokxCFF/4VURkcTmVwOJXB4XQFx09+OE66guMnPxwnLcHxkx8/nHrg+MmPHz/54TgJhr8FHAxwLygbfvpTXFy8mF8SAKDk" +
            "2/3yP6F4cX9IIECSL5XvAYoXCQIBkq0nQAnnwu/KqlEIBEj2joLWffwVwbpxRSSIe/FPpLc4aUkvLu62h9IKICBid8V7JLnjwShNNYfxRADjXrzp2v1epREu" +
            "MouLk5NDop4ABBh3joNI7rhVZBGSk0MyQgACjMcVENLOPR7JXgWnOYwjgoNB7sWbpEeEIJKWtum8iYud5jCOCA6GuDdHTdIRdHKLW/kMZR0uNs1h/BAcDHEv" +
            "JKYrPqtKRNI0qiKjd6lpDuOHAMS9gPi7YtJ5xJrWNpWYvCub5jB+CA6GuBcQf1f8aqUlkkc8tUtm9qFINccMkfBPBHFQ3hNbU65pts6nK2uaQ0Ig+fyUxQBw" +
            "BlkwW+3058GXrRcsICsmQWn8ynDeY6/AB4LbVBtYYJGUfMOWS/kIu/ayRQ1ziuISAmjFLEhtwNRedquOS+dPfUIArZhM1AQWrL2csOpHbSOqodiEAFoxwdQB" +
            "/LWXkDK2G37VIhqLSwigFZMtpUem2sshylxx1NJV/V4rK1NMgFYMQsmRufZy0LIktx2+7VV+rIwmgFYMQskRVHs5VNZ05Uctuw7SQCsGoeQIrr3cN9kyVGwz" +
            "YtFAK0YfNAiuvURuZc/CWKndKEArpg/e4mW1l7NkZ0ou36AV0wn3eFntZSv7mTkdoBXTCrf04trLV3IwN2jF9MJjmkTlLTkCU0p4XMVw2w6N32a4kGa4kGa4" +
            "kGa4kGa4kGa4kGa4kGa4kGa4kGa4kGa4kGa4kGa4kGa4kF7YqgE=";

        private const string SearchBrotliBase64 =
            "G/8D+CcB/vxBBwd24eRgCQXkAQUT9dcBPJHAKfcrVsCl2oEmqHipD8+lbgrtl/ID";
    }
}

